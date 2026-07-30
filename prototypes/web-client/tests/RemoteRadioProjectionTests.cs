using System.Text.Json;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RemoteRadioProjectionTests
{
    [Fact]
    public void ProjectedSnapshotCanNeverEnableTransmit()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        PanadapterSnapshot pan = new(
            14_100_000,
            200_000,
            -130,
            -40,
            Id: "0x40000000",
            StreamId: 0x40000000);
        RadioSnapshot projected = new(
            99,
            "station-private-session",
            "FLEX-6400",
            "1234-5678",
            true,
            true,
            "A",
            pan,
            [
                new SliceSnapshot(
                    "A",
                    14_074_000,
                    "USB",
                    300,
                    3_000,
                    50,
                    0,
                    true,
                    false,
                    PanStreamId: pan.StreamId)
            ],
            [pan],
            "connected");

        Assert.True(coordinator.ApplyReceiveProjection(projected));
        Assert.True(coordinator.Snapshot.Connected);
        Assert.False(coordinator.Snapshot.CanTransmit);
        Assert.Equal("central-session", coordinator.Snapshot.SessionId);
        Assert.Equal(14_074_000, coordinator.Snapshot.Slices[0].FrequencyHz);
    }

    [Fact]
    public async Task RemoteIntentUsesOnlyEnumeratedBrowserControl()
    {
        RemoteRadioIntentRouter router = new();
        string? sentAction = null;
        using IDisposable lease = router.Attach(
            (payload, _) =>
            {
                using JsonDocument request =
                    JsonDocument.Parse(payload);
                long id = request.RootElement
                    .GetProperty("id")
                    .GetInt64();
                sentAction = request.RootElement
                    .GetProperty("action")
                    .GetString();
                using JsonDocument response = JsonDocument.Parse(
                    JsonSerializer.Serialize(
                        new
                        {
                            id,
                            ok = true,
                            version = 12,
                            model = "slice",
                            selector = "A",
                            changes = new
                            {
                                frequencyHz = 14_074_000
                            }
                        }));
                Assert.True(
                    router.TryHandleResponse(
                        response.RootElement));
                return Task.CompletedTask;
            });
        using JsonDocument values =
            JsonDocument.Parse("""{"frequencyHz":14074000}""");

        IntentResult result = await router.ApplyAsync(
            new ControlIntent(
                "slice.set",
                "A",
                values.RootElement.Clone()),
            10,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("slice.set", sentAction);
        Assert.Equal(12, result.Version);
    }

    [Fact]
    public async Task RemoteIntentRejectsTransmitBeforeNetworkSend()
    {
        RemoteRadioIntentRouter router = new();
        int sends = 0;
        using IDisposable lease = router.Attach(
            (_, _) =>
            {
                sends++;
                return Task.CompletedTask;
            });
        using JsonDocument values =
            JsonDocument.Parse("""{"mox":true}""");

        IntentResult result = await router.ApplyAsync(
            new ControlIntent(
                "transmit.set",
                string.Empty,
                values.RootElement.Clone()),
            10,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, sends);
    }

    private static RadioCoordinator CreateCoordinator() =>
        new(
            NullLogger<RadioCoordinator>.Instance,
            Options.Create(
                new RadioSettings
                {
                    Mode = "Remote",
                    SessionId = "central-session",
                    AllowTransmit = false
                }),
            new TxLeaseManager(),
            new FlexRadioCommandRouter());
}
