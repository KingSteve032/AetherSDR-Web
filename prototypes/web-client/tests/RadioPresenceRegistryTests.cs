using System.Net.WebSockets;
using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RadioPresenceRegistryTests
{
    [Fact]
    public void DifferentAccountsOnOneRadioSeeEachOther()
    {
        RadioPresenceRegistry registry = new();
        RadioCoordinator firstCoordinator = CreateCoordinator("session-a");
        RadioCoordinator secondCoordinator = CreateCoordinator("session-b");
        RadioClientConnection first = CreateConnection(
            "client-a",
            "user-a",
            "Operator A");
        RadioClientConnection second = CreateConnection(
            "client-b",
            "user-b",
            "Operator B");

        registry.Register("radio-1", firstCoordinator, first);
        Drain(first);
        registry.Register("radio-1", secondCoordinator, second);

        OperatorPresenceSnapshot[] snapshot =
            registry.GetSnapshot("radio-1").ToArray();
        Assert.Equal(2, snapshot.Length);
        Assert.Collection(
            snapshot,
            person =>
            {
                Assert.Equal("user-a", person.UserId);
                Assert.Equal(1, person.ConnectionCount);
            },
            person =>
            {
                Assert.Equal("user-b", person.UserId);
                Assert.Equal(1, person.ConnectionCount);
            });
        AssertPresenceUsers(first, "user-a", "user-b");
        AssertPresenceUsers(second, "user-a", "user-b");
    }

    [Fact]
    public void MultipleConnectionsForOneAccountAreAggregated()
    {
        RadioPresenceRegistry registry = new();
        RadioCoordinator coordinator = CreateCoordinator("session-a");
        RadioClientConnection first = CreateConnection(
            "client-a",
            "user-a",
            "Operator A");
        RadioClientConnection second = CreateConnection(
            "client-b",
            "user-a",
            "Operator A");

        registry.Register("radio-1", coordinator, first);
        Drain(first);
        registry.Register("radio-1", coordinator, second);

        OperatorPresenceSnapshot presence =
            Assert.Single(registry.GetSnapshot("radio-1"));
        Assert.Equal("user-a", presence.UserId);
        Assert.Equal(2, presence.ConnectionCount);
        AssertPresenceConnectionCount(first, 2);
        AssertPresenceConnectionCount(second, 2);
    }

    [Fact]
    public void PresenceDoesNotCrossPhysicalRadioBoundary()
    {
        RadioPresenceRegistry registry = new();
        RadioClientConnection first = CreateConnection(
            "client-a",
            "user-a",
            "Operator A");
        RadioClientConnection second = CreateConnection(
            "client-b",
            "user-b",
            "Operator B");

        registry.Register(
            "radio-1",
            CreateCoordinator("session-a"),
            first);
        Drain(first);
        registry.Register(
            "radio-2",
            CreateCoordinator("session-b"),
            second);

        Assert.Equal(
            "user-a",
            Assert.Single(registry.GetSnapshot("radio-1")).UserId);
        Assert.Equal(
            "user-b",
            Assert.Single(registry.GetSnapshot("radio-2")).UserId);
        AssertPresenceUsers(second, "user-b");
        Assert.False(first.Outbox.TryRead(out _));
    }

    [Fact]
    public void DisconnectRemovesOnlyThatBrowserConnection()
    {
        RadioPresenceRegistry registry = new();
        RadioCoordinator coordinator = CreateCoordinator("session-a");
        RadioClientConnection first = CreateConnection(
            "client-a",
            "user-a",
            "Operator A");
        RadioClientConnection second = CreateConnection(
            "client-b",
            "user-a",
            "Operator A");

        registry.Register("radio-1", coordinator, first);
        Drain(first);
        registry.Register("radio-1", coordinator, second);
        Drain(first);
        Drain(second);
        registry.Unregister("radio-1", first.ClientId);

        OperatorPresenceSnapshot remaining =
            Assert.Single(registry.GetSnapshot("radio-1"));
        Assert.Equal(1, remaining.ConnectionCount);
        AssertPresenceConnectionCount(second, 1);

        registry.Unregister("radio-1", second.ClientId);
        Assert.Empty(registry.GetSnapshot("radio-1"));
    }

    [Fact]
    public void WelcomeCanBeQueuedBeforeRadioWidePresence()
    {
        RadioPresenceRegistry registry = new();
        RadioCoordinator coordinator = CreateCoordinator("session-a");
        RadioClientConnection connection = CreateConnection(
            "client-a",
            "user-a",
            "Operator A");
        IReadOnlyList<OperatorPresenceSnapshot> initial =
            registry.Preview("radio-1", connection.ToPresence());

        coordinator.SendJson(
            connection,
            new { type = "welcome", presence = initial });
        registry.Register("radio-1", coordinator, connection);

        Assert.True(connection.Outbox.TryRead(out OutboundMessage? first));
        using JsonDocument welcome = JsonDocument.Parse(first.Payload);
        Assert.Equal(
            "welcome",
            welcome.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            1,
            welcome.RootElement.GetProperty("presence").GetArrayLength());

        Assert.True(connection.Outbox.TryRead(out OutboundMessage? second));
        using JsonDocument presence = JsonDocument.Parse(second.Payload);
        Assert.Equal(
            "presence",
            presence.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public async Task ForceDisconnectCompletesOnlyTheSelectedUsersConnections()
    {
        RadioPresenceRegistry registry = new();
        RadioClientConnection first = CreateConnection(
            "client-a",
            "user-a",
            "Operator A");
        RadioClientConnection second = CreateConnection(
            "client-b",
            "user-b",
            "Operator B");
        RadioCoordinator coordinator = CreateCoordinator("session-a");
        registry.Register("radio-1", coordinator, first);
        registry.Register("radio-1", coordinator, second);
        Drain(first);
        Drain(second);

        int disconnected = registry.ForceDisconnect("radio-1", "user-a");

        Assert.Equal(1, disconnected);
        Assert.True(first.Outbox.TryRead(out OutboundMessage? message));
        using (JsonDocument document = JsonDocument.Parse(message.Payload))
        {
            Assert.Equal(
                "admin.disconnected",
                document.RootElement.GetProperty("event").GetString());
        }
        Assert.False(await first.Outbox.WaitToReadAsync());
        Assert.True(second.TryEnqueue(
            new OutboundMessage(
                WebSocketMessageType.Text,
                "{}"u8.ToArray())));
    }

    private static void AssertPresenceUsers(
        RadioClientConnection connection,
        params string[] expectedUserIds)
    {
        Assert.True(connection.Outbox.TryRead(out OutboundMessage? message));
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        using JsonDocument document = JsonDocument.Parse(message.Payload);
        JsonElement root = document.RootElement;
        Assert.Equal(
            "presence",
            root.GetProperty("event").GetString());
        string[] userIds = root
            .GetProperty("clients")
            .EnumerateArray()
            .Select(client => client.GetProperty("userId").GetString()!)
            .ToArray();
        Assert.Equal(expectedUserIds, userIds);
    }

    private static void AssertPresenceConnectionCount(
        RadioClientConnection connection,
        int expected)
    {
        Assert.True(connection.Outbox.TryRead(out OutboundMessage? message));
        using JsonDocument document = JsonDocument.Parse(message.Payload);
        JsonElement presence = Assert.Single(
            document.RootElement
                .GetProperty("clients")
                .EnumerateArray()
                .ToArray());
        Assert.Equal(
            expected,
            presence.GetProperty("connectionCount").GetInt32());
    }

    private static void Drain(RadioClientConnection connection)
    {
        while (connection.Outbox.TryRead(out _))
        {
        }
    }

    private static RadioClientConnection CreateConnection(
        string clientId,
        string userId,
        string displayName) =>
        new(
            clientId,
            userId,
            displayName,
            [AetherRoles.Control]);

    private static RadioCoordinator CreateCoordinator(string sessionId) =>
        new(
            NullLogger<RadioCoordinator>.Instance,
            Options.Create(
                new RadioSettings
                {
                    Mode = "Simulation",
                    SessionId = sessionId
                }),
            new TxLeaseManager());
}
