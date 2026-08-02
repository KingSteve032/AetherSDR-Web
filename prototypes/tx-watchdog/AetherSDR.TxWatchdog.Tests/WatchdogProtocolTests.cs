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
        Assert.Null(request.HeartbeatTimeoutMilliseconds);
    }

    [Fact]
    public void ArmRequiresOneBoundedServerOwnedHeartbeatTimeout()
    {
        string missing = RequestJson(
            "arm",
            "arm-missing",
            2,
            IdentityJson("radio-a"));
        string valid = RequestJson(
            "arm",
            "arm-valid",
            2,
            IdentityJson("radio-a"),
            heartbeatTimeoutMilliseconds: 1000);
        string tooLarge = RequestJson(
            "arm",
            "arm-large",
            2,
            IdentityJson("radio-a"),
            heartbeatTimeoutMilliseconds:
                WatchdogProtocol.MaximumHeartbeatTimeoutMilliseconds + 1);

        Assert.False(WatchdogProtocol.TryParseRequest(
            missing,
            out _,
            out string missingError));
        Assert.Equal("arm-requires-heartbeat-timeout", missingError);
        Assert.True(WatchdogProtocol.TryParseRequest(
            valid,
            out WatchdogRequest? request,
            out string validError), validError);
        Assert.Equal(WatchdogRequestKind.Arm, request!.Kind);
        Assert.Equal(1000, request.HeartbeatTimeoutMilliseconds);
        Assert.False(WatchdogProtocol.TryParseRequest(
            tooLarge,
            out _,
            out string largeError));
        Assert.Equal("invalid-heartbeat-timeout", largeError);
    }

    [Fact]
    public void StatusCannotCarryAuthorityOrDeadlineFields()
    {
        string sequence = $$"""
            {
              "protocolVersion": {{WatchdogProtocol.Version}},
              "requestId": "request-2",
              "type": "status",
              "sequence": 1
            }
            """;
        string timeout = $$"""
            {
              "protocolVersion": {{WatchdogProtocol.Version}},
              "requestId": "request-3",
              "type": "status",
              "heartbeatTimeoutMilliseconds": 1000
            }
            """;

        Assert.False(WatchdogProtocol.TryParseRequest(
            sequence,
            out _,
            out string sequenceError));
        Assert.Equal("status-must-not-carry-authority", sequenceError);
        Assert.False(WatchdogProtocol.TryParseRequest(
            timeout,
            out _,
            out string timeoutError));
        Assert.Equal("status-must-not-carry-authority", timeoutError);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("unkey")]
    [InlineData("lease")]
    [InlineData("reset")]
    [InlineData("command")]
    public void KeyAndArbitraryMutationTypesAreNotInTheProtocol(string type)
    {
        string json = RequestJson(
            type,
            requestId: "request-4",
            sequence: 1,
            IdentityJson("radio-a"));

        Assert.False(WatchdogProtocol.TryParseRequest(
            json,
            out _,
            out string error));
        Assert.Equal("unknown-request-type", error);
    }

    [Fact]
    public void RegisterDisarmAndDisconnectRejectHeartbeatTimeouts()
    {
        foreach (string type in new[] { "register", "disarm", "disconnect" })
        {
            string json = RequestJson(
                type,
                $"{type}-timeout",
                1,
                IdentityJson("radio-a"),
                heartbeatTimeoutMilliseconds: 1000);

            Assert.False(WatchdogProtocol.TryParseRequest(
                json,
                out _,
                out string error));
            Assert.Equal("unexpected-heartbeat-timeout", error);
        }
    }

    [Fact]
    public void UnknownOrDuplicatePropertiesAreRejectedAtTheBoundary()
    {
        string unknown = $$"""
            {
              "protocolVersion": {{WatchdogProtocol.Version}},
              "requestId": "request-5",
              "type": "status",
              "unexpected": true
            }
            """;
        string duplicate = $$"""
            {
              "protocolVersion": {{WatchdogProtocol.Version}},
              "requestId": "request-6",
              "requestId": "request-7",
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
    public void ArmedRequestsRoundTripThroughTheStrictParser()
    {
        WatchdogRequest expected = new(
            WatchdogProtocol.Version,
            "request-roundtrip",
            WatchdogRequestKind.Arm,
            Sequence: 7,
            Identity("RADIO-A"),
            HeartbeatTimeoutMilliseconds: 1500);

        string json = WatchdogProtocol.SerializeRequest(expected);

        Assert.True(WatchdogProtocol.TryParseRequest(
            json,
            out WatchdogRequest? actual,
            out string error), error);
        Assert.Equal(expected, actual);
        Assert.Contains("\"type\":\"arm\"", json, StringComparison.Ordinal);
        Assert.Contains(
            "\"heartbeatTimeoutMilliseconds\":1500",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StrictResponseParserAcceptsDisabledDisarmedSnapshot()
    {
        WatchdogResponse expected = Response(new WatchdogSnapshot(
            "watchdog-a",
            new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero),
            "Disarmed",
            "unkey-transport-disabled-disarmed",
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
                8,
                2,
                10,
                0,
                1,
                TimeSpan.Zero)));

        string json = WatchdogProtocol.SerializeResponse(expected);

        Assert.True(WatchdogProtocol.TryParseResponse(
            json,
            out WatchdogResponse? actual,
            out string error), error);
        Assert.NotNull(actual);
        Assert.Equal("Disarmed", actual.Snapshot.State);
        Assert.False(actual.Snapshot.Armed);
        Assert.False(actual.Snapshot.RadioCommandTransportAvailable);
        Assert.Null(actual.Snapshot.Identity);
    }

    [Fact]
    public void StrictResponseParserAcceptsExactArmedSnapshot()
    {
        DateTimeOffset armedAt =
            new(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        WatchdogResponse expected = Response(new WatchdogSnapshot(
            "watchdog-armed",
            armedAt,
            "Armed",
            "armed-heartbeat-current",
            RadioCommandTransportAvailable: true,
            ArmingAvailable: true,
            Registered: true,
            Connected: true,
            Identity: Identity(),
            LeaseBound: true,
            LastSequence: 2,
            LastObservation: "armed-exact-authority",
            LastObservedAt: armedAt,
            Armed: true,
            ArmedAt: armedAt,
            LastHeartbeatAt: armedAt,
            HeartbeatDeadlineAt: armedAt.AddSeconds(1),
            HeartbeatTimeoutMilliseconds: 1000,
            UnkeyAttemptCount: 0,
            UnkeyAcceptedCount: 0,
            UnkeyRejectedCount: 0,
            UnkeyUnknownCount: 0,
            LastUnkeyOutcome: "none",
            LastUnkeyReason: "none"));

        string json = WatchdogProtocol.SerializeResponse(expected);

        Assert.True(WatchdogProtocol.TryParseResponse(
            json,
            out WatchdogResponse? actual,
            out string error), error);
        Assert.NotNull(actual);
        Assert.Equal("Armed", actual.Snapshot.State);
        Assert.True(actual.Snapshot.Armed);
        Assert.True(actual.Snapshot.ArmingAvailable);
        Assert.Equal(1000, actual.Snapshot.HeartbeatTimeoutMilliseconds);
        Assert.Null(actual.Snapshot.Identity);
    }

    [Theory]
    [InlineData("\"armingAvailable\":false", "\"armingAvailable\":true")]
    [InlineData("\"registered\":true", "\"registered\":false")]
    [InlineData("\"leaseBound\":true", "\"leaseBound\":false")]
    public void InconsistentDisarmedResponsesAreRejected(
        string current,
        string replacement)
    {
        WatchdogResponse valid = Response(new WatchdogSnapshot(
            "watchdog-a",
            DateTimeOffset.UnixEpoch,
            "Disarmed",
            "unkey-transport-disabled-disarmed",
            RadioCommandTransportAvailable: false,
            ArmingAvailable: false,
            Registered: true,
            Connected: true,
            Identity: Identity(),
            LeaseBound: true,
            LastSequence: 1,
            LastObservation: "registered-disarmed",
            LastObservedAt: DateTimeOffset.UnixEpoch));
        string json = WatchdogProtocol.SerializeResponse(valid).Replace(
            current,
            replacement,
            StringComparison.Ordinal);

        Assert.False(WatchdogProtocol.TryParseResponse(
            json,
            out _,
            out string error));
        Assert.Equal("invalid-response-shape", error);
    }

    [Fact]
    public void ArmedStateWithoutArmedFieldsIsRejected()
    {
        WatchdogResponse valid = Response(new WatchdogSnapshot(
            "watchdog-a",
            DateTimeOffset.UnixEpoch,
            "Disarmed",
            "watchdog-arming-ready-disarmed",
            RadioCommandTransportAvailable: true,
            ArmingAvailable: true,
            Registered: false,
            Connected: false,
            Identity: null,
            LeaseBound: false,
            LastSequence: 0,
            LastObservation: "process-started-disarmed",
            LastObservedAt: null));
        string json = WatchdogProtocol.SerializeResponse(valid).Replace(
            "\"state\":\"Disarmed\"",
            "\"state\":\"Armed\"",
            StringComparison.Ordinal);

        Assert.False(WatchdogProtocol.TryParseResponse(
            json,
            out _,
            out string error));
        Assert.Equal("invalid-response-shape", error);
    }

    [Fact]
    public void OverflowingOutcomeCountersAreRejectedWithoutThrowing()
    {
        WatchdogResponse response = Response(new WatchdogSnapshot(
            "watchdog-a",
            DateTimeOffset.UnixEpoch,
            "Disarmed",
            "deadline-unkey-accepted",
            RadioCommandTransportAvailable: true,
            ArmingAvailable: true,
            Registered: true,
            Connected: true,
            Identity: Identity(),
            LeaseBound: true,
            LastSequence: 4,
            LastObservation: "explicit-disarm-observed",
            LastObservedAt: DateTimeOffset.UnixEpoch,
            Armed: false,
            ArmedAt: null,
            LastHeartbeatAt: null,
            HeartbeatDeadlineAt: null,
            HeartbeatTimeoutMilliseconds: null,
            UnkeyAttemptCount: long.MaxValue,
            UnkeyAcceptedCount: long.MaxValue,
            UnkeyRejectedCount: long.MaxValue,
            UnkeyUnknownCount: long.MaxValue,
            LastUnkeyOutcome: "unknown",
            LastUnkeyReason: "deadline-unkey-outcome-unknown"));
        string json = WatchdogProtocol.SerializeResponse(response);

        Exception? exception = Record.Exception(() =>
            Assert.False(WatchdogProtocol.TryParseResponse(
                json,
                out _,
                out string error)));

        Assert.Null(exception);
        Assert.False(WatchdogProtocol.TryParseResponse(
            json,
            out _,
            out string parseError));
        Assert.Equal("invalid-response-shape", parseError);
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

    private static WatchdogResponse Response(WatchdogSnapshot snapshot) =>
        new(
            WatchdogProtocol.Version,
            "response-1",
            Ok: true,
            Error: null,
            snapshot);

    internal static string RequestJson(
        string type,
        string requestId,
        long sequence,
        string identityJson,
        int? heartbeatTimeoutMilliseconds = null)
    {
        string timeout = heartbeatTimeoutMilliseconds.HasValue
            ? $",\"heartbeatTimeoutMilliseconds\":{heartbeatTimeoutMilliseconds.Value}"
            : string.Empty;
        return
            $"{{\"protocolVersion\":{WatchdogProtocol.Version}," +
            $"\"requestId\":\"{requestId}\",\"type\":\"{type}\"," +
            $"\"sequence\":{sequence},\"identity\":{identityJson}{timeout}}}";
    }

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
