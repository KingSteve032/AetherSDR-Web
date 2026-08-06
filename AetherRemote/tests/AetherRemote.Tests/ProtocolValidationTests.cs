using AetherRemote.Protocol;

namespace AetherRemote.Tests;

public sealed class ProtocolValidationTests
{
    [Fact]
    public void HelloMustMatchAuthenticatedStation()
    {
        StationHelloMessage hello = new(
            StationMessageTypes.Hello,
            StationProtocol.Version,
            "station-one",
            "instance-one",
            "0.1.0");

        Assert.Null(
            StationProtocolValidator.ValidateHello(
                hello,
                "station-one"));
        Assert.NotNull(
            StationProtocolValidator.ValidateHello(
                hello,
                "station-two"));
    }

    [Fact]
    public void InventoryRejectsDuplicateRadioIds()
    {
        StationRadioAdvertisement radio = ValidRadio("flex:1234");
        StationInventoryMessage inventory = new(
            StationMessageTypes.Inventory,
            1,
            [radio, radio]);

        string? error =
            StationProtocolValidator.ValidateInventory(inventory);

        Assert.Contains("duplicate", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InventoryRejectsCapacityBeyondRadioLimit()
    {
        StationRadioAdvertisement radio =
            ValidRadio("flex:1234") with
            {
                AvailableClients = 3,
                LicensedClients = 2
            };

        string? error =
            StationProtocolValidator.ValidateInventory(
                new StationInventoryMessage(
                    StationMessageTypes.Inventory,
                    1,
                    [radio]));

        Assert.Contains("capacity", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiveMessageVocabularyHasNoTransmitPath()
    {
        string[] messageTypes =
        [
            StationMessageTypes.Hello,
            StationMessageTypes.Inventory,
            StationMessageTypes.Heartbeat,
            StationMessageTypes.ReceiveSessionOpened,
            StationMessageTypes.ReceiveSessionClosed,
            StationMessageTypes.ReceiveSessionError,
            StationMessageTypes.ReceiveText,
            StationMessageTypes.ReceiveBinary,
            StationMessageTypes.Welcome,
            StationMessageTypes.Error,
            StationMessageTypes.OpenReceiveSession,
            StationMessageTypes.CloseReceiveSession,
            StationMessageTypes.SendReceiveText
        ];

        Assert.DoesNotContain(
            messageTypes,
            type =>
                type.Contains("tx", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("transmit", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("ptt", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("mox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReceiveProjectionAllowsOnlyEnumeratedReceiveIntents()
    {
        string valid =
            """
            {"id":3,"cmd":"intent","action":"slice.set","payload":{"sliceId":0,"frequencyHz":14074000}}
            """;
        string transmit =
            """
            {"id":4,"cmd":"intent","action":"transmit.set","payload":{"mox":true}}
            """;
        string generic =
            """
            {"id":5,"cmd":"command","payload":"transmit tune 1"}
            """;

        Assert.Null(
            StationProtocolValidator.ValidateClientProjectionCommand(
                valid));
        Assert.NotNull(
            StationProtocolValidator.ValidateClientProjectionCommand(
                transmit));
        Assert.NotNull(
            StationProtocolValidator.ValidateClientProjectionCommand(
                generic));
    }

    [Fact]
    public void ReleaseServiceControlAcceptsOnlyExactFixedUnitOperations()
    {
        BrokerReleaseServiceControlMessage valid = new(
            StationMessageTypes.ReleaseServiceControl,
            "0123456789abcdef0123456789abcdef",
            "aethersdr-8.3.0",
            "pre-switch-stop",
            "stop",
            "aetherremote-agent",
            "aetherremote-agent.service");

        Assert.Null(
            StationProtocolValidator.ValidateReleaseServiceControl(valid));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseServiceControl(
                valid with { UnitIdentity = "../../evil.service" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseServiceControl(
                valid with { Action = "restart" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseServiceControl(
                valid with { Phase = "post-switch-start" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseServiceControl(
                valid with { ServiceRole = "gateway-web" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseServiceControl(
                valid with { ReleaseIdentity = "release-latest" }));
    }

    [Fact]
    public void ReleaseServiceControlResultMustEchoExactRequestShape()
    {
        StationReleaseServiceControlResultMessage valid = new(
            StationMessageTypes.ReleaseServiceControlResult,
            "0123456789abcdef0123456789abcdef",
            "aethersdr-8.3.0",
            "post-switch-start",
            "start",
            "station-engine",
            "aetherremote-station-engine.service",
            Succeeded: true,
            Outcome: "completed");

        Assert.Null(
            StationProtocolValidator.ValidateReleaseServiceControlResult(valid));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseServiceControlResult(
                valid with { Outcome = "contains spaces" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseServiceControlResult(
                valid with { CorrelationId = "short" }));
    }

    [Fact]
    public void ReceiveProjectionBinaryRequiresKnownBoundedFraming()
    {
        byte[] valid = new byte[16];
        "AETF"u8.CopyTo(valid);
        StationReceiveBinaryMessage message = new(
            StationMessageTypes.ReceiveBinary,
            "0123456789abcdef0123456789abcdef",
            Convert.ToBase64String(valid));

        Assert.Null(
            StationProtocolValidator.ValidateReceiveBinary(
                message,
                out byte[] decoded));
        Assert.Equal(valid, decoded);
        Assert.NotNull(
            StationProtocolValidator.ValidateReceiveBinary(
                message with
                {
                    PayloadBase64 =
                        Convert.ToBase64String("MOXX"u8)
                },
                out _));
    }

    [Fact]
    public void ReceiveSessionOpenRequiresBoundedOpaqueIdentities()
    {
        BrokerOpenReceiveSessionMessage valid = new(
            StationMessageTypes.OpenReceiveSession,
            "0123456789abcdef0123456789abcdef",
            "flex:1234",
            Guid.NewGuid().ToString(),
            false);

        Assert.Null(
            StationProtocolValidator.ValidateOpenReceiveSession(valid));
        Assert.NotNull(
            StationProtocolValidator.ValidateOpenReceiveSession(
                valid with { SessionId = "../station-secret" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateOpenReceiveSession(
                valid with { GuiClientId = "not-a-guid" }));
    }

    private static StationRadioAdvertisement ValidRadio(string radioId) =>
        new(
            radioId,
            "flex",
            "FLEX-6700",
            "1234-5678",
            "Test Radio",
            "available",
            2,
            2,
            string.Empty);
}
