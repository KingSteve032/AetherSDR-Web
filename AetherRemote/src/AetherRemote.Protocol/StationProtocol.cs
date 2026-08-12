using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AetherRemote.Protocol;

public static class StationProtocol
{
    public const int Version = 1;
    public const string WebSocketSubprotocol = "aetherremote.station.v1";
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumRadiosPerStation = 32;
    public const int MaximumStationIdLength = 64;
    public const int MaximumRadioIdLength = 128;
    public const int MaximumProjectionTextBytes = 48 * 1024;
    public const int MaximumProjectionBinaryBytes = 32 * 1024;

    public static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow
        };
}

public static class StationMessageTypes
{
    public const string Hello = "station.hello";
    public const string Inventory = "station.inventory";
    public const string Heartbeat = "station.heartbeat";
    public const string ReceiveSessionOpened = "station.receive.opened";
    public const string ReceiveSessionClosed = "station.receive.closed";
    public const string ReceiveSessionError = "station.receive.error";
    public const string ReceiveText = "station.receive.text";
    public const string ReceiveBinary = "station.receive.binary";
    public const string Welcome = "broker.welcome";
    public const string Error = "broker.error";
    public const string OpenReceiveSession = "broker.receive.open";
    public const string CloseReceiveSession = "broker.receive.close";
    public const string SendReceiveText = "broker.receive.text";
    public const string ReleaseServiceControl =
        "broker.release.service-control";
    public const string ReleaseServiceControlResult =
        "station.release.service-control-result";
    public const string ReleaseUpdate = "broker.release.update";
    public const string ReleaseUpdateResult = "station.release.update-result";
    public const string ReleaseUpdateAcknowledgement =
        "broker.release.update-ack";
}

public static class StationLocalUpdaterMessageTypes
{
    public const string Request = "local.release.update";
    public const string Result = "local.release.update-result";
}

public static class StationLocalUpdaterActions
{
    public const string Apply = "apply";
    public const string Rollback = "rollback";
    public const string Confirm = "confirm";
    public const string Acknowledge = "acknowledge";
}

public static class StationCapabilities
{
    public const string ReceiveProjectionV1 = "receive-projection-v1";
    public const string ReleaseServiceControlV1 =
        "release-service-control-v1";
    public const string ReleaseUpdateV1 = "release-update-v1";

    public static bool IsKnown(string? capability) =>
        capability is ReceiveProjectionV1 or ReleaseServiceControlV1 or
            ReleaseUpdateV1;
}

public sealed record StationLinkTokenRequest(
    string StationId,
    IReadOnlyList<string>? Capabilities);

public sealed record StationLinkTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Capabilities);

public sealed record StationHelloMessage(
    string Type,
    int ProtocolVersion,
    string StationId,
    string InstanceId,
    string SoftwareVersion,
    IReadOnlyList<string>? Capabilities = null,
    string ReleaseIdentity = "",
    string StationEngineVersion = "");

public sealed record StationInventoryMessage(
    string Type,
    long Sequence,
    IReadOnlyList<StationRadioAdvertisement> Radios);

public sealed record StationHeartbeatMessage(
    string Type,
    long Sequence);

public sealed record BrokerWelcomeMessage(
    string Type,
    int ProtocolVersion,
    string ConnectionId,
    int HeartbeatSeconds,
    int MaximumMessageBytes);

public sealed record BrokerErrorMessage(
    string Type,
    string Code,
    string Message);

public sealed record BrokerOpenReceiveSessionMessage(
    string Type,
    string SessionId,
    string RadioId,
    string GuiClientId,
    bool LowBandwidth);

public sealed record BrokerCloseReceiveSessionMessage(
    string Type,
    string SessionId);

public sealed record StationReceiveSessionOpenedMessage(
    string Type,
    string SessionId,
    string RadioId,
    string RadioModel,
    string Serial,
    string ClientHandle);

public sealed record StationReceiveSessionClosedMessage(
    string Type,
    string SessionId,
    string Reason);

public sealed record StationReceiveSessionErrorMessage(
    string Type,
    string SessionId,
    string Code,
    string Message);

public sealed record StationReceiveTextMessage(
    string Type,
    string SessionId,
    string Payload);

public sealed record StationReceiveBinaryMessage(
    string Type,
    string SessionId,
    string PayloadBase64);

public sealed record BrokerReceiveTextMessage(
    string Type,
    string SessionId,
    string Payload);

public sealed record BrokerReleaseServiceControlMessage(
    string Type,
    string CorrelationId,
    string ReleaseIdentity,
    string Phase,
    string Action,
    string ServiceRole,
    string UnitIdentity);

public sealed record StationReleaseServiceControlResultMessage(
    string Type,
    string CorrelationId,
    string ReleaseIdentity,
    string Phase,
    string Action,
    string ServiceRole,
    string UnitIdentity,
    bool Succeeded,
    string Outcome);

public sealed record BrokerReleaseUpdateMessage(
    string Type,
    string CorrelationId,
    string ReleaseIdentity);

public sealed record StationReleaseUpdateResultMessage(
    string Type,
    string CorrelationId,
    string ReleaseIdentity,
    bool Succeeded,
    string Outcome,
    string ActiveReleaseIdentity,
    bool RolledBack);

public sealed record BrokerReleaseUpdateAcknowledgementMessage(
    string Type,
    string CorrelationId,
    string ReleaseIdentity);

public sealed record LocalStationReleaseUpdateRequest(
    string Type,
    string CorrelationId,
    string ReleaseIdentity,
    string Action);

public sealed record LocalStationReleaseUpdateResult(
    string Type,
    string CorrelationId,
    string ReleaseIdentity,
    string Action,
    bool Succeeded,
    string Outcome,
    string ActiveReleaseIdentity,
    string PreviousReleaseIdentity,
    bool RequiresAgentRestart,
    string CompletedReleaseIdentity = "",
    bool RolledBack = false);

public sealed record StationRadioAdvertisement(
    string RadioId,
    string Family,
    string Model,
    string Serial,
    string Nickname,
    string Status,
    int AvailableClients,
    int LicensedClients,
    string CapabilityHash);

public static partial class StationProtocolValidator
{
    public static string? ValidateLinkTokenRequest(
        StationLinkTokenRequest? request,
        string authenticatedStationId)
    {
        if (request is null ||
            !IsIdentifier(
                request.StationId,
                StationProtocol.MaximumStationIdLength) ||
            !string.Equals(
                request.StationId,
                authenticatedStationId,
                StringComparison.Ordinal))
        {
            return "The station identity does not match its credential.";
        }
        IReadOnlyList<string>? capabilities = request.Capabilities;
        if (capabilities is null ||
            capabilities.Count > 16 ||
            capabilities.Any(
                capability =>
                    !IsIdentifier(capability, 64) ||
                    !StationCapabilities.IsKnown(capability)) ||
            capabilities.Distinct(StringComparer.Ordinal).Count() !=
            capabilities.Count)
        {
            return "A valid supported capability list is required.";
        }
        return null;
    }

    public static bool CapabilitiesMatch(
        IReadOnlyList<string>? advertised,
        IReadOnlyList<string> authorized)
    {
        if (advertised is null || advertised.Count != authorized.Count)
        {
            return false;
        }
        return advertised
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .SequenceEqual(
                authorized.OrderBy(
                    capability => capability,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    public static string? ValidateHello(
        StationHelloMessage? message,
        string authenticatedStationId)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.Hello,
                StringComparison.Ordinal))
        {
            return "The first station message must be station.hello.";
        }
        if (message.ProtocolVersion != StationProtocol.Version)
        {
            return "The station protocol version is not supported.";
        }
        if (!IsIdentifier(
                message.StationId,
                StationProtocol.MaximumStationIdLength) ||
            !string.Equals(
                message.StationId,
                authenticatedStationId,
                StringComparison.Ordinal))
        {
            return "The station identity does not match its credential.";
        }
        if (!IsIdentifier(
                message.InstanceId,
                StationProtocol.MaximumStationIdLength))
        {
            return "A valid station instance ID is required.";
        }
        if (!IsText(message.SoftwareVersion, 64))
        {
            return "A valid station software version is required.";
        }
        IReadOnlyList<string> capabilities =
            message.Capabilities ?? [];
        if (capabilities.Count > 16 ||
            capabilities.Any(
                capability => !IsIdentifier(capability, 64)) ||
            capabilities.Distinct(StringComparer.Ordinal).Count() !=
            capabilities.Count)
        {
            return "A valid station capability list is required.";
        }
        bool releaseIdentityPresent =
            !string.IsNullOrEmpty(message.ReleaseIdentity);
        bool stationEngineVersionPresent =
            !string.IsNullOrEmpty(message.StationEngineVersion);
        if (releaseIdentityPresent != stationEngineVersionPresent ||
            releaseIdentityPresent &&
            (!IsIdentifier(message.ReleaseIdentity, 96) ||
             !IsText(message.StationEngineVersion, 64)))
        {
            return "Station release identity and engine version metadata are invalid.";
        }
        if (capabilities.Contains(
                StationCapabilities.ReleaseUpdateV1,
                StringComparer.Ordinal) &&
            !releaseIdentityPresent)
        {
            return "Release-update capability requires exact station release metadata.";
        }
        return null;
    }

    public static string? ValidateInventory(StationInventoryMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.Inventory,
                StringComparison.Ordinal))
        {
            return "A valid station.inventory message is required.";
        }
        if (message.Sequence < 1)
        {
            return "Inventory sequence must be positive.";
        }
        if (message.Radios is null ||
            message.Radios.Count > StationProtocol.MaximumRadiosPerStation)
        {
            return "The station radio count is outside the supported range.";
        }

        HashSet<string> radioIds = new(StringComparer.Ordinal);
        foreach (StationRadioAdvertisement radio in message.Radios)
        {
            string? error = ValidateRadio(radio);
            if (error is not null)
            {
                return error;
            }
            if (!radioIds.Add(radio.RadioId))
            {
                return "A station inventory cannot contain duplicate radio IDs.";
            }
        }
        return null;
    }

    public static string? ValidateHeartbeat(StationHeartbeatMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.Heartbeat,
                StringComparison.Ordinal))
        {
            return "A valid station.heartbeat message is required.";
        }
        return message.Sequence < 1
            ? "Heartbeat sequence must be positive."
            : null;
    }

    public static string? ValidateOpenReceiveSession(
        BrokerOpenReceiveSessionMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.OpenReceiveSession,
                StringComparison.Ordinal))
        {
            return "A valid broker.receive.open message is required.";
        }
        if (!IsSessionId(message.SessionId) ||
            !IsIdentifier(
                message.RadioId,
                StationProtocol.MaximumRadioIdLength) ||
            !Guid.TryParse(message.GuiClientId, out _))
        {
            return "The receive session identity is invalid.";
        }
        return null;
    }

    public static string? ValidateCloseReceiveSession(
        BrokerCloseReceiveSessionMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.CloseReceiveSession,
                StringComparison.Ordinal) ||
            !IsSessionId(message.SessionId))
        {
            return "A valid broker.receive.close message is required.";
        }
        return null;
    }

    public static string? ValidateReceiveSessionOpened(
        StationReceiveSessionOpenedMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReceiveSessionOpened,
                StringComparison.Ordinal) ||
            !IsSessionId(message.SessionId) ||
            !IsIdentifier(
                message.RadioId,
                StationProtocol.MaximumRadioIdLength) ||
            !IsText(message.RadioModel, 64) ||
            !IsText(message.Serial, 64) ||
            !IsClientHandle(message.ClientHandle))
        {
            return "The station receive-session acknowledgement is invalid.";
        }
        return null;
    }

    public static string? ValidateReceiveSessionClosed(
        StationReceiveSessionClosedMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReceiveSessionClosed,
                StringComparison.Ordinal) ||
            !IsSessionId(message.SessionId) ||
            !IsText(message.Reason, 128))
        {
            return "The station receive-session closure is invalid.";
        }
        return null;
    }

    public static string? ValidateReceiveSessionError(
        StationReceiveSessionErrorMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReceiveSessionError,
                StringComparison.Ordinal) ||
            !IsSessionId(message.SessionId) ||
            !IsIdentifier(message.Code, 64) ||
            !IsText(message.Message, 256))
        {
            return "The station receive-session error is invalid.";
        }
        return null;
    }

    public static string? ValidateReceiveText(
        StationReceiveTextMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReceiveText,
                StringComparison.Ordinal) ||
            !IsSessionId(message.SessionId) ||
            !IsProjectionJson(message.Payload))
        {
            return "The station receive text frame is invalid.";
        }
        return null;
    }

    public static string? ValidateReceiveBinary(
        StationReceiveBinaryMessage? message,
        out byte[] payload)
    {
        payload = [];
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReceiveBinary,
                StringComparison.Ordinal) ||
            !IsSessionId(message.SessionId) ||
            string.IsNullOrWhiteSpace(message.PayloadBase64) ||
            message.PayloadBase64.Length >
                ((StationProtocol.MaximumProjectionBinaryBytes + 2) / 3 * 4))
        {
            return "The station receive binary frame is invalid.";
        }
        try
        {
            payload = Convert.FromBase64String(message.PayloadBase64);
        }
        catch (FormatException)
        {
            return "The station receive binary frame is invalid.";
        }
        if (!IsProjectionBinary(payload))
        {
            payload = [];
            return "The station receive binary frame is invalid.";
        }
        return null;
    }

    public static string? ValidateBrokerReceiveText(
        BrokerReceiveTextMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.SendReceiveText,
                StringComparison.Ordinal) ||
            !IsSessionId(message.SessionId))
        {
            return "The broker receive text frame is invalid.";
        }
        return ValidateClientProjectionCommand(message.Payload);
    }

    public static string? ValidateReleaseServiceControl(
        BrokerReleaseServiceControlMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReleaseServiceControl,
                StringComparison.Ordinal) ||
            !IsSessionId(message.CorrelationId) ||
            !IsReleaseIdentity(message.ReleaseIdentity) ||
            message.Phase is not "pre-switch-stop" and
                not "post-switch-start" ||
            message.Action is not "stop" and not "start" ||
            message.ServiceRole is not "aetherremote-agent" and
                not "station-engine" ||
            !IsExactReleaseUnit(message.ServiceRole, message.UnitIdentity) ||
            message.Phase == "pre-switch-stop" && message.Action != "stop" ||
            message.Phase == "post-switch-start" && message.Action != "start")
        {
            return "The remote release service-control request is invalid.";
        }
        return null;
    }

    public static string? ValidateReleaseServiceControlResult(
        StationReleaseServiceControlResultMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReleaseServiceControlResult,
                StringComparison.Ordinal) ||
            ValidateReleaseServiceControl(
                new BrokerReleaseServiceControlMessage(
                    StationMessageTypes.ReleaseServiceControl,
                    message.CorrelationId,
                    message.ReleaseIdentity,
                    message.Phase,
                    message.Action,
                    message.ServiceRole,
                    message.UnitIdentity)) is not null ||
            !IsIdentifier(message.Outcome, 64))
        {
            return "The remote release service-control result is invalid.";
        }
        return null;
    }

    public static string? ValidateReleaseUpdate(
        BrokerReleaseUpdateMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReleaseUpdate,
                StringComparison.Ordinal) ||
            !IsSessionId(message.CorrelationId) ||
            !IsReleaseIdentity(message.ReleaseIdentity))
        {
            return "The remote release-update request is invalid.";
        }
        return null;
    }

    public static string? ValidateReleaseUpdateResult(
        StationReleaseUpdateResultMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReleaseUpdateResult,
                StringComparison.Ordinal) ||
            ValidateReleaseUpdate(
                new BrokerReleaseUpdateMessage(
                    StationMessageTypes.ReleaseUpdate,
                    message.CorrelationId,
                    message.ReleaseIdentity)) is not null ||
            !IsIdentifier(message.Outcome, 64) ||
            !IsReleaseIdentity(message.ActiveReleaseIdentity) ||
            message.Succeeded &&
                (!string.Equals(
                    message.ActiveReleaseIdentity,
                    message.ReleaseIdentity,
                    StringComparison.Ordinal) ||
                 message.RolledBack) ||
            message.RolledBack && message.Succeeded)
        {
            return "The remote release-update result is invalid.";
        }
        return null;
    }

    public static string? ValidateReleaseUpdateAcknowledgement(
        BrokerReleaseUpdateAcknowledgementMessage? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationMessageTypes.ReleaseUpdateAcknowledgement,
                StringComparison.Ordinal) ||
            !IsSessionId(message.CorrelationId) ||
            !IsReleaseIdentity(message.ReleaseIdentity))
        {
            return "The remote release-update acknowledgement is invalid.";
        }
        return null;
    }

    public static string? ValidateLocalReleaseUpdateRequest(
        LocalStationReleaseUpdateRequest? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationLocalUpdaterMessageTypes.Request,
                StringComparison.Ordinal) ||
            !IsSessionId(message.CorrelationId) ||
            !IsReleaseIdentity(message.ReleaseIdentity) ||
            message.Action is not StationLocalUpdaterActions.Apply and
                not StationLocalUpdaterActions.Rollback and
                not StationLocalUpdaterActions.Confirm and
                not StationLocalUpdaterActions.Acknowledge)
        {
            return "The local station release-update request is invalid.";
        }
        return null;
    }

    public static string? ValidateLocalReleaseUpdateResult(
        LocalStationReleaseUpdateResult? message)
    {
        if (message is null ||
            !string.Equals(
                message.Type,
                StationLocalUpdaterMessageTypes.Result,
                StringComparison.Ordinal) ||
            ValidateLocalReleaseUpdateRequest(
                new LocalStationReleaseUpdateRequest(
                    StationLocalUpdaterMessageTypes.Request,
                    message.CorrelationId,
                    message.ReleaseIdentity,
                    message.Action)) is not null ||
            !IsIdentifier(message.Outcome, 64) ||
            !IsReleaseIdentity(message.ActiveReleaseIdentity) ||
            !IsReleaseIdentity(message.PreviousReleaseIdentity) ||
            (!string.IsNullOrEmpty(message.CompletedReleaseIdentity) &&
             !IsReleaseIdentity(message.CompletedReleaseIdentity)) ||
            (message.RolledBack &&
             string.IsNullOrEmpty(message.CompletedReleaseIdentity)))
        {
            return "The local station release-update result is invalid.";
        }
        return null;
    }

    private static bool IsExactReleaseUnit(string role, string unit) =>
        role switch
        {
            "aetherremote-agent" =>
                string.Equals(
                    unit,
                    "aetherremote-agent.service",
                    StringComparison.Ordinal),
            "station-engine" =>
                string.Equals(
                    unit,
                    "aetherremote-station-engine.service",
                    StringComparison.Ordinal),
            _ => false
        };

    public static string? ValidateClientProjectionCommand(string? payload)
    {
        if (!IsProjectionJson(payload))
        {
            return "The projected client command is invalid.";
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload!);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty(
                    "cmd",
                    out JsonElement commandElement) ||
                commandElement.ValueKind != JsonValueKind.String)
            {
                return "The projected client command requires a command.";
            }
            string? command = commandElement.GetString();
            if (command is "hello" or "subscribe" or "ping")
            {
                return null;
            }
            if (!string.Equals(
                    command,
                    "intent",
                    StringComparison.Ordinal) ||
                !root.TryGetProperty(
                    "action",
                    out JsonElement actionElement) ||
                actionElement.ValueKind != JsonValueKind.String)
            {
                return "That projected client command is not supported.";
            }
            string action = actionElement.GetString() ?? string.Empty;
            if (!KnownReceiveActions.Contains(action))
            {
                return "That projected receive intent is not supported.";
            }
            return null;
        }
        catch (JsonException)
        {
            return "The projected client command is malformed.";
        }
    }

    public static string? ReadMessageType(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out JsonElement typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string? type = typeElement.GetString();
        return IsText(type, 64) ? type : null;
    }

    public static bool IsIdentifier(string? value, int maximumLength) =>
        value is not null &&
        value.Length is > 0 &&
        value.Length <= maximumLength &&
        IdentifierPattern().IsMatch(value);

    public static bool IsReleaseIdentity(string? value) =>
        value is { Length: >= 15 and <= 128 } &&
        ReleaseIdentityPattern().IsMatch(value);

    public static bool IsText(string? value, int maximumLength) =>
        value is not null &&
        value.Length is > 0 &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    public static bool IsSessionId(string? value) =>
        value is { Length: 32 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsClientHandle(string? value) =>
        value is { Length: 8 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsProjectionJson(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) ||
            Encoding.UTF8.GetByteCount(payload) >
                StationProtocol.MaximumProjectionTextBytes ||
            payload.Any(character =>
                character == '\0' ||
                char.IsSurrogate(character)))
        {
            return false;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            return document.RootElement.ValueKind ==
                JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsProjectionBinary(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 16 or >
            StationProtocol.MaximumProjectionBinaryBytes)
        {
            return false;
        }
        return payload[..4].SequenceEqual("AETF"u8) ||
               payload[..4].SequenceEqual("AETA"u8);
    }

    private static string? ValidateRadio(StationRadioAdvertisement? radio)
    {
        if (radio is null ||
            !IsIdentifier(radio.RadioId, StationProtocol.MaximumRadioIdLength))
        {
            return "Every advertised radio requires a valid stable ID.";
        }
        if (!IsIdentifier(radio.Family, 32) ||
            !IsText(radio.Model, 64) ||
            !IsText(radio.Serial, 64))
        {
            return $"Radio '{radio.RadioId}' has invalid identity fields.";
        }
        if (radio.Nickname is null ||
            radio.Nickname.Length > 64 ||
            radio.Nickname.Any(char.IsControl) ||
            radio.Status is null ||
            !KnownRadioStatuses.Contains(radio.Status))
        {
            return $"Radio '{radio.RadioId}' has invalid display fields.";
        }
        if (radio.AvailableClients < -1 ||
            radio.LicensedClients < -1 ||
            radio.AvailableClients > 64 ||
            radio.LicensedClients > 64 ||
            (radio.AvailableClients >= 0 &&
             radio.LicensedClients >= 0 &&
             radio.AvailableClients > radio.LicensedClients))
        {
            return $"Radio '{radio.RadioId}' has invalid client capacity.";
        }
        if (radio.CapabilityHash is null ||
            radio.CapabilityHash.Length > 128 ||
            radio.CapabilityHash.Any(char.IsControl))
        {
            return $"Radio '{radio.RadioId}' has an invalid capability hash.";
        }
        return null;
    }

    private static readonly HashSet<string> KnownRadioStatuses =
        new(
            ["available", "in-use", "updating", "unknown"],
            StringComparer.Ordinal);

    private static readonly HashSet<string> KnownReceiveActions =
        new(
            [
                "slice.set",
                "slice.create",
                "slice.remove",
                "pan.set",
                "pan.create",
                "pan.remove"
            ],
            StringComparer.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        "^aethersdr-(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseIdentityPattern();
}
