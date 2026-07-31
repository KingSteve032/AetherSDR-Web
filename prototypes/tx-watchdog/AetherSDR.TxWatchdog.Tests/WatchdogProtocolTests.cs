using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog.Tests;

public sealed class WatchdogProtocolTests
{
    [Fact]
    public void RegisterParsesOnlyTheExactBoundedAuthorityEnvelope()
    {
        string json = RequestJson(
            "register",
            requestId: "request-1",
            sequence: 1,
            IdentityJson("radio-a"));

        Assert.True(WatchdogProtocol.TryParseRequest(
            json,
            out WatchdogRequest? request,
            out string error), error);
        Assert.NotNull(request);
        Assert.Equal(WatchdogProtocol.Version, request.ProtocolVersion);
        Assert.Equal(WatchdogRequestKind.Register, request.Kind);
        Assert.Equal(1, request.Sequence);
        Assert.Equal("RADIO-A", request.Identity!.RadioId);
        Assert.Equal("lease-a", request.Identity.LeaseId);
        Assert.Equal(0x1234abcdu, request.Identity.StationClientHandle);
    }

    [Fact]
    public void StatusCannotCarryAnAuthorityEnvelope()
    {
        string json = $$"""
            {
              "protocolVersion": {{WatchdogProtocol.Version}},
              "requestId": "request-2",
              "type": "status",
              "sequence": 1
            }
            """;

        Assert.False(WatchdogProtocol.TryParseRequest(
            json,
            out _,
            out string error));
        Assert.Equal("status-must-not-carry-authority", error);
    }

    [Theory]
    [InlineData("arm")]
    [InlineData("key")]
    [InlineData("lease")]
    [InlineData("reset")]
    public void CommandOrAuthorityMutationTypesAreNotInTheProtocol(string type)
    {
        string json = RequestJson(
            type,
            requestId: "request-3",
            sequence: 1,
            IdentityJson("radio-a"));

        Assert.False(WatchdogProtocol.TryParseRequest(
            json,
            out _,
            out string error));
        Assert.Equal("unknown-request-type", error);
    }

    [Fact]
    public void UnknownOrDuplicatePropertiesAreRejectedAtTheBoundary()
    {
        string unknown = $$"""
            {
              "protocolVersion": {{WatchdogProtocol.Version}},
              "requestId": "request-4",
              "type": "status",
              "unexpected": true
            }
            """;
        string duplicate = $$"""
            {
              "protocolVersion": {{WatchdogProtocol.Version}},
              "requestId": "request-5",
              "requestId": "request-6",
              "type": "status"
            }
            """;

        Assert.False(WatchdogProtocol.TryParseRequest(
            unknown,
            out _,
            out string unknownError));
        Assert.Equal("invalid-request-shape", unknownError);
        Assert.False(WatchdogProtocol.TryParseRequest(
            duplicate,
            out _,
            out string duplicateError));
        Assert.Equal("invalid-request-shape", duplicateError);
    }

    [Fact]
    public void OversizedMessagesAreRejectedBeforeJsonParsing()
    {
        string oversized = new(
            'x',
            WatchdogProtocol.MaximumMessageCharacters + 1);

        Assert.False(WatchdogProtocol.TryParseRequest(
            oversized,
            out _,
            out string error));
        Assert.Equal("message-too-large", error);
    }

    internal static string RequestJson(
        string type,
        string requestId,
        long sequence,
        string identityJson) =>
        $$"""{"protocolVersion":{{WatchdogProtocol.Version}},"requestId":"{{requestId}}","type":"{{type}}","sequence":{{sequence}},"identity":{{identityJson}}}""";

    internal static string StatusJson(string requestId) =>
        $$"""{"protocolVersion":{{WatchdogProtocol.Version}},"requestId":"{{requestId}}","type":"status"}""";

    internal static string IdentityJson(string radioId) =>
        $$"""{"radioId":"{{radioId}}","sessionId":"session-a","browserClientId":"browser-a","gatewayInstanceId":"gateway-a","engineInstanceId":"engine-a","connectionClientId":"connection-a","leaseId":"lease-a","stationClientHandle":305441741}""";

    internal static WatchdogIdentity Identity(string radioId = "RADIO-A") =>
        new(
            radioId,
            "session-a",
            "browser-a",
            "gateway-a",
            "engine-a",
            "connection-a",
            "lease-a",
            0x1234abcd);
}
