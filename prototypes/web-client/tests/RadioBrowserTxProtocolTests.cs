using System.Text.Json;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class RadioBrowserTxProtocolTests
{
    private const string LeaseId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void AcquireRequiresAnExactVersionedBoundedEnvelope()
    {
        BrowserTxRequest request = Parse(
            """
            {"id":1,"cmd":"tx.acquire","protocolVersion":2,"sequence":1,"seconds":10}
            """);

        Assert.Equal(1, request.RequestId);
        Assert.Equal(BrowserTxRequestKind.Acquire, request.Kind);
        Assert.Equal(1, request.Sequence);
        Assert.Equal(10, request.Seconds);
        Assert.Null(request.LeaseId);
        Assert.Null(request.Intent);
    }

    [Theory]
    [InlineData("""{"id":1,"cmd":"tx.acquire","protocolVersion":2,"sequence":1}""")]
    [InlineData("""{"id":1,"cmd":"tx.acquire","protocolVersion":2,"sequence":1,"seconds":0}""")]
    [InlineData("""{"id":1,"cmd":"tx.acquire","protocolVersion":2,"sequence":1,"seconds":16}""")]
    [InlineData("""{"id":1,"cmd":"tx.acquire","protocolVersion":1,"sequence":1,"seconds":10}""")]
    [InlineData("""{"id":0,"cmd":"tx.acquire","protocolVersion":2,"sequence":1,"seconds":10}""")]
    [InlineData("""{"id":9007199254740992,"cmd":"tx.acquire","protocolVersion":2,"sequence":1,"seconds":10}""")]
    [InlineData("""{"id":1,"cmd":"tx.acquire","protocolVersion":2,"sequence":0,"seconds":10}""")]
    [InlineData("""{"id":1,"cmd":"tx.acquire","protocolVersion":2,"sequence":9007199254740992,"seconds":10}""")]
    public void AcquireRejectsMissingDefaultedOrOutOfRangeFields(string json)
    {
        Assert.False(TryParse(json, out _, out _));
    }

    [Fact]
    public void UnknownAndDuplicatePropertiesAreRejected()
    {
        Assert.False(TryParse(
            """
            {"id":1,"cmd":"tx.acquire","protocolVersion":2,"sequence":1,"seconds":10,"extra":true}
            """,
            out _,
            out string unknownError));
        Assert.Equal("invalid-tx-acquire", unknownError);

        Assert.False(TryParse(
            """
            {"id":1,"cmd":"tx.acquire","protocolVersion":2,"sequence":1,"sequence":2,"seconds":10}
            """,
            out _,
            out string duplicateError));
        Assert.Equal("invalid-tx-acquire", duplicateError);
    }

    [Fact]
    public void RenewReleaseAndHeartbeatRequireTheExactOpaqueLeaseId()
    {
        BrowserTxRequest renew = Parse(
            $$"""{"id":2,"cmd":"tx.renew","protocolVersion":2,"sequence":2,"seconds":10,"leaseId":"{{LeaseId}}"}""");
        BrowserTxRequest release = Parse(
            $$"""{"id":3,"cmd":"tx.release","protocolVersion":2,"sequence":3,"leaseId":"{{LeaseId}}"}""");
        BrowserTxRequest heartbeat = Parse(
            $$"""{"id":4,"cmd":"tx.heartbeat","protocolVersion":2,"sequence":4,"leaseId":"{{LeaseId}}"}""");

        Assert.Equal(BrowserTxRequestKind.Renew, renew.Kind);
        Assert.Equal(LeaseId, renew.LeaseId);
        Assert.Equal(BrowserTxRequestKind.Release, release.Kind);
        Assert.Equal(LeaseId, release.LeaseId);
        Assert.Equal(BrowserTxRequestKind.Heartbeat, heartbeat.Kind);
        Assert.Equal(LeaseId, heartbeat.LeaseId);
        Assert.Null(heartbeat.Intent);

        Assert.False(TryParse(
            $$"""{"id":5,"cmd":"tx.heartbeat","protocolVersion":2,"sequence":5,"leaseId":"{{LeaseId.ToUpperInvariant()}}"}""",
            out _,
            out string error));
        Assert.Equal("invalid-tx-heartbeat", error);
    }

    [Fact]
    public void HeartbeatRejectsAdditionalProperties()
    {
        Assert.False(TryParse(
            $$"""{"id":4,"cmd":"tx.heartbeat","protocolVersion":2,"sequence":4,"leaseId":"{{LeaseId}}","enabled":true}""",
            out _,
            out string error));
        Assert.Equal("invalid-tx-heartbeat", error);
    }

    [Theory]
    [InlineData("mox.set", "{\"enabled\":true}", "Mox")]
    [InlineData("ptt.set", "{\"enabled\":false}", "Ptt")]
    [InlineData("tune.set", "{\"enabled\":true}", "Tune")]
    [InlineData("microphone.set", "{\"enabled\":false}", "Microphone")]
    public void BooleanIntentFamiliesParseExactly(
        string action,
        string values,
        string expectedKind)
    {
        BrowserTxRequest request = Parse(IntentJson(action, values));

        BrowserTxIntent intent = Assert.IsType<BrowserTxIntent>(request.Intent);
        Assert.Equal(BrowserTxRequestKind.Intent, request.Kind);
        Assert.Equal(expectedKind, intent.Kind.ToString());
        Assert.Equal(action, intent.Action);
        Assert.NotNull(intent.Enabled);
        Assert.Null(intent.Text);
    }

    [Fact]
    public void CwIntentAcceptsOnlyBoundedPrintableText()
    {
        BrowserTxRequest request = Parse(IntentJson(
            "cw.send",
            "{\"text\":\"CQ TEST KC4CAW\"}"));
        BrowserTxIntent intent = Assert.IsType<BrowserTxIntent>(request.Intent);

        Assert.Equal(BrowserTxIntentKind.Cw, intent.Kind);
        Assert.Equal("CQ TEST KC4CAW", intent.Text);
        Assert.Null(intent.Enabled);

        Assert.False(TryParse(
            IntentJson("cw.send", "{\"text\":\"line\\nbreak\"}"),
            out _,
            out _));
        Assert.False(TryParse(
            IntentJson("cw.send", $"{{\"text\":\"{new string('A', 33)}\"}}"),
            out _,
            out _));
    }

    [Fact]
    public void IntentIdUsesOnlyConservativeIdentifierCharacters()
    {
        string invalid = IntentJson("mox.set", "{\"enabled\":true}")
            .Replace(
                "intent-000000000000000000000000000001",
                "intent with spaces",
                StringComparison.Ordinal);

        Assert.False(TryParse(invalid, out _, out string error));
        Assert.Equal("invalid-tx-intent", error);
    }

    [Theory]
    [InlineData("mox.set", "{}")]
    [InlineData("ptt.set", "{\"enabled\":true,\"extra\":1}")]
    [InlineData("tune.set", "{\"enabled\":1}")]
    [InlineData("microphone.set", "{\"enabled\":null}")]
    [InlineData("cw.send", "{\"text\":\"\"}")]
    [InlineData("unknown.set", "{\"enabled\":true}")]
    public void InvalidIntentPayloadsAreRejected(string action, string values)
    {
        Assert.False(TryParse(IntentJson(action, values), out _, out string error));
        Assert.Equal("invalid-tx-intent", error);
    }

    [Fact]
    public void PerConnectionSequenceAndIntentReplayAreFailClosed()
    {
        RadioClientConnection connection = NewConnection();

        Assert.True(connection.TryAcceptTxEnvelope(1, null, out string firstError), firstError);
        Assert.False(connection.TryAcceptTxEnvelope(1, null, out string staleError));
        Assert.Equal("stale-tx-sequence", staleError);

        Assert.True(connection.TryAcceptTxEnvelope(2, "intent-a", out string intentError), intentError);
        Assert.False(connection.TryAcceptTxEnvelope(3, "intent-a", out string replayError));
        Assert.Equal("replayed-tx-intent", replayError);
        Assert.Equal(3, connection.LastTxSequence);
        Assert.False(connection.TryAcceptTxEnvelope(3, "intent-b", out string consumedError));
        Assert.Equal("stale-tx-sequence", consumedError);
    }

    [Fact]
    public void IntentReplayMemoryIsBoundedWithoutWeakeningSequenceOrdering()
    {
        RadioClientConnection connection = NewConnection();
        for (int index = 0; index <= RadioClientConnection.TxIntentReplayCapacity; index++)
        {
            Assert.True(connection.TryAcceptTxEnvelope(
                index + 1,
                $"intent-{index}",
                out string error), error);
        }

        Assert.True(connection.TryAcceptTxEnvelope(
            RadioClientConnection.TxIntentReplayCapacity + 2,
            "intent-0",
            out string recycledError), recycledError);
        Assert.Equal(
            RadioClientConnection.TxIntentReplayCapacity + 2,
            connection.LastTxSequence);
    }

    private static BrowserTxRequest Parse(string json)
    {
        Assert.True(TryParse(json, out BrowserTxRequest? request, out string error), error);
        return Assert.IsType<BrowserTxRequest>(request);
    }

    private static bool TryParse(
        string json,
        out BrowserTxRequest? request,
        out string error)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return RadioBrowserTxProtocol.TryParse(
            document.RootElement,
            out request,
            out error);
    }

    private static string IntentJson(string action, string values) =>
        $$"""{"id":4,"cmd":"tx.intent","protocolVersion":2,"sequence":4,"leaseId":"{{LeaseId}}","intentId":"intent-000000000000000000000000000001","action":"{{action}}","values":{{values}}}""";

    private static RadioClientConnection NewConnection() =>
        new(
            "connection-a",
            "operator-a",
            "Operator A",
            ["Aether.Transmit"]);
}
