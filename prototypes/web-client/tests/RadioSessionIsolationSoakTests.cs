using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RadioSessionIsolationSoakTests
{
    private const string BrowserA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BrowserB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    [Trait("Category", "Soak")]
    public async Task ConcurrentBrowserSessionsRemainIsolatedAcrossRepeatedControl()
    {
        (
            RadioSessionRegistry registry,
            RadioSelectionManager catalog) = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            ClaimsPrincipal userA = CreateUser("operator-a");
            ClaimsPrincipal userB = CreateUser("operator-b");
            RadioSession sessionA = await registry.GetDefaultAsync(
                userA,
                BrowserA,
                CancellationToken.None);
            RadioSession sessionB = await registry.GetDefaultAsync(
                userB,
                BrowserB,
                CancellationToken.None);

            Assert.NotEqual(sessionA.SessionId, sessionB.SessionId);
            Assert.NotEqual(sessionA.GuiClientId, sessionB.GuiClientId);
            Assert.Equal(sessionA.Endpoint.RadioId, sessionB.Endpoint.RadioId);
            Assert.False(sessionA.Coordinator.Snapshot.CanTransmit);
            Assert.False(sessionB.Coordinator.Snapshot.CanTransmit);

            RadioClientConnection clientA =
                sessionA.Coordinator.Register(userA);
            RadioClientConnection clientB =
                sessionB.Coordinator.Register(userB);
            try
            {
                Drain(clientA);
                Drain(clientB);
                for (int iteration = 0; iteration < 250; iteration++)
                {
                    long frequencyA = 14_050_000 + (iteration * 10);
                    long frequencyB = 14_150_000 - (iteration * 10);
                    AssertSuccessfulTune(
                        sessionA.Coordinator,
                        frequencyA);
                    AssertSuccessfulTune(
                        sessionB.Coordinator,
                        frequencyB);
                    Assert.Equal(
                        frequencyA,
                        SliceA(sessionA).FrequencyHz);
                    Assert.Equal(
                        frequencyB,
                        SliceA(sessionB).FrequencyHz);
                }

                Drain(clientA);
                Drain(clientB);
                sessionA.Coordinator.BroadcastJson(
                    new { isolationMarker = sessionA.SessionId });
                Assert.Equal(
                    sessionA.SessionId,
                    ReadIsolationMarker(clientA));
                Assert.Null(TryReadIsolationMarker(clientB));

                sessionB.Coordinator.BroadcastJson(
                    new { isolationMarker = sessionB.SessionId });
                Assert.Equal(
                    sessionB.SessionId,
                    ReadIsolationMarker(clientB));
                Assert.Null(TryReadIsolationMarker(clientA));

                RadioSessionDiagnostics diagnosticsA =
                    await WaitForSpectrumAsync(sessionA);
                RadioSessionDiagnostics diagnosticsB =
                    await WaitForSpectrumAsync(sessionB);
                Assert.Equal("Simulation", diagnosticsA.Transport.Transport);
                Assert.Equal("Simulation", diagnosticsB.Transport.Transport);
                Assert.True(diagnosticsA.Transport.SpectrumFrames > 0);
                Assert.True(diagnosticsB.Transport.SpectrumFrames > 0);
                Assert.Single(diagnosticsA.WebClients);
                Assert.Single(diagnosticsB.WebClients);
                Assert.NotEqual(
                    diagnosticsA.WebClients[0].ClientId,
                    diagnosticsB.WebClients[0].ClientId);
                Assert.All(
                    registry.GetDiagnostics(),
                    diagnostic =>
                        Assert.Equal(
                            catalog.Selected.RadioId,
                            diagnostic.RadioId));
            }
            finally
            {
                sessionA.Coordinator.Unregister(clientA.ClientId);
                sessionB.Coordinator.Unregister(clientB.ClientId);
            }

            Assert.True(
                registry.TryAcquire(
                    sessionA.SessionId,
                    userA,
                    out RadioSession? reconnected));
            Assert.Same(sessionA, reconnected);
            sessionA.ReleaseClient();
            Assert.Same(
                sessionA,
                await registry.GetDefaultAsync(
                    userA,
                    BrowserA,
                    CancellationToken.None));
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    private static SliceSnapshot SliceA(RadioSession session) =>
        Assert.Single(
            session.Coordinator.Snapshot.Slices,
            slice => slice.Id == "A");

    private static void AssertSuccessfulTune(
        RadioCoordinator coordinator,
        long frequencyHz)
    {
        using JsonDocument values = JsonDocument.Parse(
            $$"""{"frequencyHz":{{frequencyHz}}}""");
        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent(
                "slice.set",
                "A",
                values.RootElement.Clone()));
        Assert.True(result.Ok, result.Error);
    }

    private static async Task<RadioSessionDiagnostics> WaitForSpectrumAsync(
        RadioSession session)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            RadioSessionDiagnostics diagnostics = session.GetDiagnostics();
            if (diagnostics.Transport.SpectrumFrames > 0)
            {
                return diagnostics;
            }
            await Task.Delay(20);
        }

        return session.GetDiagnostics();
    }

    private static string ReadIsolationMarker(
        RadioClientConnection connection) =>
        TryReadIsolationMarker(connection) ??
        throw new Xunit.Sdk.XunitException(
            "The isolated session marker was not queued.");

    private static string? TryReadIsolationMarker(
        RadioClientConnection connection)
    {
        while (connection.Outbox.TryRead(out OutboundMessage? message))
        {
            connection.MarkDequeued();
            if (message.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(message.Payload);
            if (document.RootElement.TryGetProperty(
                    "isolationMarker",
                    out JsonElement marker))
            {
                return marker.GetString();
            }
        }
        return null;
    }

    private static void Drain(RadioClientConnection connection)
    {
        while (connection.Outbox.TryRead(out _))
        {
            connection.MarkDequeued();
        }
    }

    private static (
        RadioSessionRegistry Registry,
        RadioSelectionManager Catalog) CreateRegistry()
    {
        IOptions<RadioSettings> options = Options.Create(
            new RadioSettings
            {
                Mode = "Simulation",
                Host = "192.168.7.10",
                TcpPort = 4992,
                SessionId = "unused-global-session"
            });
        RadioSelectionManager catalog = new(options);
        RadioAccessPolicyStore policies = new(
            Path.Combine(
                Path.GetTempPath(),
                "aethersdr-web-tests",
                Guid.NewGuid().ToString("N"),
                "policies.json"),
            NullLogger<RadioAccessPolicyStore>.Instance);
        RadioSessionRegistry registry = new(
            catalog,
            policies,
            options,
            new TxLeaseManager(),
            new RadioTxOccupancyRegistry(),
            NullLoggerFactory.Instance,
            NullLogger<RadioSessionRegistry>.Instance);
        return (registry, catalog);
    }

    private static ClaimsPrincipal CreateUser(string userId) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim("oid", userId),
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    new Claim(ClaimTypes.Role, AetherRoles.Control)
                ],
                authenticationType: "test"));
}
