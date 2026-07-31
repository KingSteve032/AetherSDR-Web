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
    public void SerializedRequestsRoundTripThroughTheStrictParser()
    {
        WatchdogRequest expected = new(
            WatchdogProtocol.Version,
            "request-roundtrip",
            WatchdogRequestKind.Heartbeat,
            Sequence: 7,
            Identity("RADIO-A"));

        string json = WatchdogProtocol.SerializeRequest(expected);

        Assert.True(WatchdogProtocol.TryParseRequest(
            json,
            out WatchdogRequest? actual,
            out string error), error);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("arm", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrictResponseParserAcceptsOnlyCommandIncapableSnapshots()
    {
        WatchdogResponse expected = new(
            WatchdogProtocol.Version,
            "response-1",
            Ok: true,
            Error: null,
            new WatchdogSnapshot(
                "watchdog-a",
                new DateTimeOffset(2026, 7, 31, 13, 0, 0, TimeSpan.Zero),
                "Disarmed",
                "command-incapable-skeleton",
                RadioCommandTransportAvailable: false,
                ArmingAvailable: false,
                Registered: true,
                Connected: true,
                Identity: Identity(),
                LeaseBound: true,
                LastSequence: 4,
                LastObservation: "heartbeat-observed-disarmed",
                LastObservedAt: new DateTimeOffset(
                    2026,
                    7,
                    31,
                    13,
                    0,
                    1,
                    TimeSpan.Zero)));

        string json = WatchdogProtocol.SerializeResponse(expected);

        Assert.True(WatchdogProtocol.TryParseResponse(
            json,
            out WatchdogResponse? actual,
            out string error), error);
        Assert.NotNull(actual);
        Assert.True(actual.Ok);
        Assert.Equal("watchdog-a", actual.Snapshot.HostInstanceId);
        Assert.True(actual.Snapshot.Registered);
        Assert.True(actual.Snapshot.Connected);
        Assert.True(actual.Snapshot.LeaseBound);
        Assert.False(actual.Snapshot.RadioCommandTransportAvailable);
        Assert.False(actual.Snapshot.ArmingAvailable);
        Assert.Null(actual.Snapshot.Identity);
    }

    [Theory]
    [InlineData("radioCommandTransportAvailable", "true")]
    [InlineData("armingAvailable", "true")]
    [InlineData("registered", "false")]
    [InlineData("leaseBound", "false")]
    public void UnsafeOrInconsistentResponsesAreRejected(
        string property,
        string replacement)
    {
        string json =
            "{\"protocolVersion\":1,\"requestId\":\"response-2\",\"ok\":true," +
            "\"snapshot\":{\"hostInstanceId\":\"watchdog-a\"," +
            "\"startedAt\":\"2026-07-31T13:00:00+00:00\"," +
            "\"state\":\"Disarmed\",\"reason\":\"command-incapable-skeleton\"," +
            "\"radioCommandTransportAvailable\":false,\"armingAvailable\":false," +
            "\"registered\":true,\"connected\":true,\"leaseBound\":true," +
            "\"lastSequence\":1,\"lastObservation\":\"registered-disarmed\"," +
            "\"lastObservedAt\":\"2026-07-31T13:00:01+00:00\"}}";
        json = json.Replace(
            $"\"{property}\":{(property is "registered" or "leaseBound" ? "true" : "false")}",
            $"\"{property}\":{replacement}",
            StringComparison.Ordinal);

        Assert.False(WatchdogProtocol.TryParseResponse(
            json,
            out _,
            out string error));
        Assert.Equal("invalid-response-shape", error);
    }

    [Theory]
    [InlineData("\"state\":\"Disarmed\"", "\"state\":\"Armed\"")]
    [InlineData(
        "\"reason\":\"command-incapable-skeleton\"",
        "\"reason\":\"authority-restored\"")]
    public void NonDisarmedResponseStatesAreRejected(
        string current,
        string replacement)
    {
        string json =
            "{\"protocolVersion\":1,\"requestId\":\"response-3\",\"ok\":true," +
            "\"snapshot\":{\"hostInstanceId\":\"watchdog-a\"," +
            "\"startedAt\":\"2026-07-31T13:00:00+00:00\"," +
            "\"state\":\"Disarmed\",\"reason\":\"command-incapable-skeleton\"," +
            "\"radioCommandTransportAvailable\":false,\"armingAvailable\":false," +
            "\"registered\":false,\"connected\":false,\"leaseBound\":false," +
            "\"lastSequence\":0,\"lastObservation\":\"process-started-disarmed\"}}";

        Assert.False(WatchdogProtocol.TryParseResponse(
            json.Replace(current, replacement, StringComparison.Ordinal),
            out _,
            out string error));
        Assert.Equal("invalid-response-shape", error);
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
