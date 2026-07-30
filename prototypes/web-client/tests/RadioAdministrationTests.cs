using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RadioAdministrationTests
{
    [Fact]
    public void RadioHealthClassifiesOfflineBusyAndHealthyInventory()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        AdminRadioHealthSnapshot offline = RadioHealthClassifier.Classify(
            CreateRadio(online: false, availableClients: 2),
            [],
            now);
        AdminRadioHealthSnapshot busy = RadioHealthClassifier.Classify(
            CreateRadio(online: true, availableClients: 0),
            [],
            now);
        AdminRadioHealthSnapshot healthy = RadioHealthClassifier.Classify(
            CreateRadio(online: true, availableClients: 1),
            [],
            now);

        Assert.Equal(AdminRadioHealthStates.Offline, offline.State);
        Assert.Equal(AdminRadioHealthStates.Busy, busy.State);
        Assert.Equal(AdminRadioHealthStates.Healthy, healthy.State);
    }

    [Fact]
    public void RadioHealthPrioritizesReconnectAndStaleHeartbeat()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RadioSessionDiagnostics current = CreateSession(
            "session-one",
            "Operator One",
            0x1A2B3C4D,
            []);
        RadioSessionDiagnostics reconnecting = current with
        {
            Connected = false,
            ConnectionState = "reconnecting",
            ConnectionError = "The radio connection stopped."
        };
        RadioSessionDiagnostics stale = current with
        {
            CreatedAt = now - TimeSpan.FromMinutes(1),
            Transport = current.Transport with
            {
                ConnectedAt = now - TimeSpan.FromMinutes(1),
                LastHeartbeatAt = now - TimeSpan.FromSeconds(30)
            }
        };

        AdminRadioHealthSnapshot reconnectHealth =
            RadioHealthClassifier.Classify(
                CreateRadio(online: true, availableClients: 1),
                [reconnecting],
                now);
        AdminRadioHealthSnapshot staleHealth =
            RadioHealthClassifier.Classify(
                CreateRadio(online: true, availableClients: 1),
                [stale],
                now);

        Assert.Equal(
            AdminRadioHealthStates.Reconnecting,
            reconnectHealth.State);
        Assert.Equal(AdminRadioHealthStates.Degraded, staleHealth.State);
        Assert.Contains("Heartbeat", staleHealth.Summary);
    }

    [Fact]
    public void RadioHealthDetectsBrowserQueuePressure()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RadioSessionDiagnostics current = CreateSession(
            "session-one",
            "Operator One",
            0x1A2B3C4D,
            []);
        RadioSessionDiagnostics pressured = current with
        {
            WebClients =
            [
                new RadioClientQueueDiagnostics(
                    "browser-one",
                    now - TimeSpan.FromMinutes(2),
                    48,
                    64,
                    100,
                    0,
                    now,
                    now,
                    null,
                    null)
            ]
        };

        AdminRadioHealthSnapshot health = RadioHealthClassifier.Classify(
            CreateRadio(online: true, availableClients: 1),
            [pressured],
            now);

        Assert.Equal(AdminRadioHealthStates.Degraded, health.State);
        Assert.Equal(48, health.QueueDepth);
        Assert.Equal(64, health.QueueCapacity);
        Assert.Contains("queue pressure", health.Summary);
    }

    [Fact]
    public void CapacityHistoryRecordsChangesAndPeriodicCheckpoints()
    {
        RadioCapacityHistoryService history = CreateCapacityHistoryService();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        history.RecordSnapshot([CreateRadio(online: true, availableClients: 2)], now);
        history.RecordSnapshot(
            [CreateRadio(online: true, availableClients: 2)],
            now + TimeSpan.FromMinutes(1));
        history.RecordSnapshot(
            [CreateRadio(online: true, availableClients: 2)],
            now + TimeSpan.FromMinutes(16));
        history.RecordSnapshot(
            [CreateRadio(online: true, availableClients: 1)],
            now + TimeSpan.FromMinutes(17));

        AdminRadioCapacitySample[] samples =
            history.GetHistory("radio-id").ToArray();
        Assert.Equal(3, samples.Length);
        Assert.Equal([2, 2, 1], samples.Select(sample => sample.AvailableClients));
        Assert.Equal(now, samples[0].ObservedAt);
        Assert.Equal(now + TimeSpan.FromMinutes(16), samples[1].ObservedAt);
        Assert.Equal(now + TimeSpan.FromMinutes(17), samples[2].ObservedAt);
    }

    [Fact]
    public void CapacityHistoryIsBoundedAndPrunesExpiredSamples()
    {
        RadioCapacityHistoryService history = CreateCapacityHistoryService();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index <= 300; index++)
        {
            history.RecordSnapshot(
                [CreateRadio(
                    online: true,
                    availableClients: index % 2)],
                now + TimeSpan.FromSeconds(index));
        }

        AdminRadioCapacitySample[] bounded =
            history.GetHistory("radio-id").ToArray();
        Assert.Equal(
            RadioCapacityHistoryService.MaximumSamplesPerRadio,
            bounded.Length);
        Assert.Equal(now + TimeSpan.FromSeconds(45), bounded[0].ObservedAt);
        Assert.Equal(now + TimeSpan.FromSeconds(300), bounded[^1].ObservedAt);

        history.RecordSnapshot(
            [CreateRadio(online: false, availableClients: 2)],
            now + TimeSpan.FromHours(25));

        AdminRadioCapacitySample remaining = Assert.Single(
            history.GetHistory("radio-id"));
        Assert.False(remaining.Online);
        Assert.Equal(2, remaining.AvailableClients);
    }

    [Fact]
    public void ConnectedClientInventoryDeduplicatesAndClassifiesRadioRoster()
    {
        const uint webHandle = 0x1A2B3C4D;
        const uint smartSdrHandle = 0x7594C952;
        RadioGuiClientDiagnostics[] roster =
        [
            new(
                webHandle,
                "web-gui-id",
                "AetherSDR",
                "AETHER-WEB-RX",
                "10.2.0.254",
                false,
                true),
            new(
                smartSdrHandle,
                "smartsdr-gui-id",
                "SmartSDR",
                "DESKTOP",
                "10.2.0.25",
                true,
                false)
        ];
        RadioSessionDiagnostics session = CreateSession(
            "session-one",
            "Operator One",
            webHandle,
            roster);

        AdminRadioGuiClientSnapshot[] clients =
            RadioAdministrationService.BuildConnectedClients([session])
                .ToArray();

        Assert.Equal(2, clients.Length);
        AdminRadioGuiClientSnapshot web = Assert.Single(
            clients,
            client => client.ClientHandle == webHandle);
        AdminRadioGuiClientSnapshot smartSdr = Assert.Single(
            clients,
            client => client.ClientHandle == smartSdrHandle);
        Assert.True(web.BrowserOwned);
        Assert.Equal("session-one", web.SessionId);
        Assert.Equal("Operator One", web.OperatorName);
        Assert.False(smartSdr.BrowserOwned);
        Assert.Equal("SmartSDR", smartSdr.Program);
        Assert.True(smartSdr.LocalPtt);
    }

    private static RadioCapacityHistoryService CreateCapacityHistoryService() =>
        new(
            new RadioSelectionManager(Options.Create(new RadioSettings())),
            NullLogger<RadioCapacityHistoryService>.Instance);

    private static RadioSelectionOption CreateRadio(
        bool online,
        int availableClients) =>
        new(
            "radio-id",
            "Test Radio",
            "FLEX-6700",
            "serial",
            "10.2.0.12",
            4992,
            availableClients == 0 ? "In_Use" : "Available",
            "4.2.18",
            online,
            true,
            online && availableClients > 0,
            false,
            false,
            availableClients,
            2);

    private static RadioSessionDiagnostics CreateSession(
        string sessionId,
        string operatorName,
        uint ownHandle,
        IReadOnlyList<RadioGuiClientDiagnostics> roster)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new RadioSessionDiagnostics(
            sessionId,
            "web-gui-id",
            "operator-id",
            operatorName,
            "radio-id",
            "10.2.0.12",
            4992,
            now,
            now,
            1,
            new RadioBrowserReconnectDiagnostics(
                1,
                1,
                0,
                0,
                now,
                null,
                null),
            false,
            1,
            true,
            "connected",
            null,
            "FLEX-6700",
            "serial",
            new RadioTransportDiagnostics(
                "FlexRx",
                ownHandle,
                42_000,
                0x04000001,
                1,
                10,
                20,
                30,
                now,
                now,
                now,
                now,
                now,
                roster),
            [],
            [],
            [],
            new RadioTxOccupancySnapshot(
                "RADIO-ID",
                RadioTxOccupancyState.Unknown,
                null,
                null,
                [],
                []),
            new RadioTuneTimingDiagnostics(
                "idle",
                string.Empty,
                -1,
                0,
                null,
                null,
                null,
                null));
    }
}
