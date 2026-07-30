using System.Text.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RadioCoordinatorTests
{
    [Fact]
    public void ValidSliceIntentUpdatesSharedSnapshot()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"frequencyHz":14274000,"mode":"DIGU"}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "A", values.RootElement.Clone()));

        Assert.True(result.Ok);
        Assert.Equal(2, result.Version);
        SliceSnapshot slice =
            Assert.Single(coordinator.Snapshot.Slices, item => item.Id == "A");
        Assert.Equal(14_274_000, slice.FrequencyHz);
        Assert.Equal("DIGU", slice.Mode);
    }

    [Fact]
    public void InvalidFrequencyIsRejectedWithoutChangingVersion()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"frequencyHz":999999999}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "A", values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Equal(1, coordinator.Snapshot.Version);
        Assert.Equal(14_263_000, coordinator.Snapshot.Slices[0].FrequencyHz);
    }

    [Fact]
    public void UsbFilterCannotBeDraggedBelowTheCarrier()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse(
                """{"filterLowHz":-100,"filterHighHz":3000}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "A", values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Contains("USB", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, coordinator.Snapshot.Version);
    }

    [Fact]
    public void LsbFilterCannotBeDraggedAboveTheCarrier()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument modeValues =
            JsonDocument.Parse("""{"mode":"LSB"}""");
        Assert.True(
            coordinator.ApplyIntent(
                new ControlIntent(
                    "slice.set",
                    "A",
                    modeValues.RootElement.Clone())).Ok);
        using JsonDocument filterValues =
            JsonDocument.Parse(
                """{"filterLowHz":-3000,"filterHighHz":100}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent(
                "slice.set",
                "A",
                filterValues.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Contains("LSB", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, coordinator.Snapshot.Version);
    }

    [Fact]
    public void FilterWidthCannotCollapseBelowRadioMinimum()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse(
                """{"filterLowHz":300,"filterHighHz":325}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "A", values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Contains("50 Hz", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, coordinator.Snapshot.Version);
    }

    [Fact]
    public void UnknownAndTransmitShapedPropertiesAreRejected()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values = JsonDocument.Parse("""{"mox":true}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "A", values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Contains("not controllable", result.Error, StringComparison.Ordinal);
        Assert.False(coordinator.Snapshot.CanTransmit);
    }

    [Fact]
    public void PrototypeCannotAcquireTxLeaseEvenWhenConfigIsArmed()
    {
        RadioCoordinator coordinator = CreateCoordinator(allowTransmit: true);
        RadioClientConnection connection = new(
            "client-a",
            "user-a",
            "Operator A",
            ["Aether.Transmit"]);

        bool acquired = coordinator.TryAcquireTxLease(
            connection,
            TimeSpan.FromSeconds(30),
            out TxLease? lease,
            out string? error);

        Assert.False(acquired);
        Assert.Null(lease);
        Assert.Contains("fail-closed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimulatedSlicesCanBeCreatedAndRemoved()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"frequencyHz":14275000,"mode":"USB"}""");

        IntentResult created = coordinator.ApplyIntent(
            new ControlIntent(
                "slice.create",
                string.Empty,
                values.RootElement.Clone()));

        Assert.True(created.Ok);
        Assert.Equal(3, coordinator.Snapshot.Slices.Count);
        Assert.Equal("C", coordinator.Snapshot.ActiveSliceId);

        using JsonDocument empty = JsonDocument.Parse("{}");
        IntentResult removed = coordinator.ApplyIntent(
            new ControlIntent(
                "slice.remove",
                "C",
                empty.RootElement.Clone()));

        Assert.True(removed.Ok);
        Assert.Equal(2, coordinator.Snapshot.Slices.Count);
        Assert.DoesNotContain(
            coordinator.Snapshot.Slices,
            slice => slice.Id == "C");
    }

    [Fact]
    public void SimulatedPanCanMoveAcrossTheBand()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"centerFrequencyHz":14050000}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent(
                "pan.set",
                string.Empty,
                values.RootElement.Clone()));

        Assert.True(result.Ok);
        Assert.Equal(
            14_050_000,
            coordinator.Snapshot.Panadapter.CenterFrequencyHz);
    }

    [Fact]
    public void SimulatedPanZoomChangesCenterAndBandwidthTogether()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values = JsonDocument.Parse(
            """
            {
              "centerFrequencyHz": 14263000,
              "bandwidthHz": 100000
            }
            """);

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent(
                "pan.set",
                string.Empty,
                values.RootElement.Clone()));

        Assert.True(result.Ok);
        Assert.Equal(
            14_263_000,
            coordinator.Snapshot.Panadapter.CenterFrequencyHz);
        Assert.Equal(
            100_000,
            coordinator.Snapshot.Panadapter.BandwidthHz);
    }

    [Fact]
    public void SimulatedBandSelectionMovesPanAndActiveSliceTogether()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"bandKey":"40"}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent(
                "pan.set",
                string.Empty,
                values.RootElement.Clone()));

        Assert.True(result.Ok);
        Assert.Equal(
            7_150_000,
            coordinator.Snapshot.Panadapter.CenterFrequencyHz);
        SliceSnapshot active = Assert.Single(
            coordinator.Snapshot.Slices,
            slice => slice.Id == coordinator.Snapshot.ActiveSliceId);
        Assert.Equal(7_150_000, active.FrequencyHz);
        Assert.Equal("LSB", active.Mode);
        Assert.Equal(-3_000, active.FilterLowHz);
        Assert.Equal(-300, active.FilterHighHz);
    }

    [Fact]
    public void InvalidBandSelectionIsRejectedAtTheBoundary()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        long originalCenter =
            coordinator.Snapshot.Panadapter.CenterFrequencyHz;
        using JsonDocument values =
            JsonDocument.Parse("""{"bandKey":"20 center=0"}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent(
                "pan.set",
                string.Empty,
                values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Equal(
            originalCenter,
            coordinator.Snapshot.Panadapter.CenterFrequencyHz);
    }

    [Fact]
    public void SimulatedPanadaptersCanBeCreatedTargetedAndRemoved()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument createValues =
            JsonDocument.Parse("""{"centerFrequencyHz":7074000}""");

        IntentResult created = coordinator.ApplyIntent(
            new ControlIntent(
                "pan.create",
                string.Empty,
                createValues.RootElement.Clone()));

        Assert.True(created.Ok);
        PanadapterSnapshot[] pans =
            Assert.IsType<PanadapterSnapshot[]>(
                coordinator.Snapshot.Panadapters);
        Assert.Equal(2, pans.Length);
        PanadapterSnapshot second = pans[1];
        Assert.Equal(7_074_000, second.CenterFrequencyHz);
        Assert.NotEqual(pans[0].StreamId, second.StreamId);

        using JsonDocument moveValues =
            JsonDocument.Parse("""{"centerFrequencyHz":7100000}""");
        IntentResult moved = coordinator.ApplyIntent(
            new ControlIntent(
                "pan.set",
                second.Id,
                moveValues.RootElement.Clone()));

        Assert.True(moved.Ok);
        Assert.Equal(
            7_100_000,
            coordinator.Snapshot.Panadapters![1].CenterFrequencyHz);
        Assert.NotEqual(
            coordinator.Snapshot.Panadapter.CenterFrequencyHz,
            coordinator.Snapshot.Panadapters[1].CenterFrequencyHz);

        using JsonDocument empty = JsonDocument.Parse("{}");
        IntentResult removed = coordinator.ApplyIntent(
            new ControlIntent(
                "pan.remove",
                second.Id,
                empty.RootElement.Clone()));

        Assert.True(removed.Ok);
        Assert.Single(coordinator.Snapshot.Panadapters!);
    }

    [Fact]
    public void ReceiveControlsUpdateTheSimulatedSlice()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values = JsonDocument.Parse(
            """
            {
              "audioPan": 72,
              "agcMode": "FAST",
              "agcThreshold": 58,
              "rxAntenna": "RX_A",
              "daxChannel": 3,
              "nb": true,
              "nbLevel": 63,
              "nrs": true,
              "nrsLevel": 44,
              "anft": true
            }
            """);

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "A", values.RootElement.Clone()));

        Assert.True(result.Ok);
        SliceSnapshot slice =
            Assert.Single(coordinator.Snapshot.Slices, item => item.Id == "A");
        Assert.Equal(72, slice.AudioPan);
        Assert.Equal("FAST", slice.AgcMode);
        Assert.Equal(58, slice.AgcThreshold);
        Assert.Equal("RX_A", slice.RxAntenna);
        Assert.Equal(3, slice.DaxChannel);
        Assert.True(slice.Nb);
        Assert.Equal(63, slice.NbLevel);
        Assert.True(slice.Nrs);
        Assert.Equal(44, slice.NrsLevel);
        Assert.True(slice.Anft);
    }

    [Fact]
    public void SelectingSliceMakesItRadioActive()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values = JsonDocument.Parse("""{"isActive":true}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "B", values.RootElement.Clone()));

        Assert.True(result.Ok);
        SliceSnapshot selected =
            Assert.Single(coordinator.Snapshot.Slices, item => item.Id == "B");
        Assert.True(selected.IsActive);
        Assert.All(
            coordinator.Snapshot.Slices.Where(item => item.Id != "B"),
            item => Assert.False(item.IsActive));
    }

    [Fact]
    public void TuningSliceAlsoMakesItRadioActive()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values = JsonDocument.Parse(
            """{"isActive":true,"frequencyHz":14074000}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "B", values.RootElement.Clone()));

        Assert.True(result.Ok);
        SliceSnapshot selected =
            Assert.Single(coordinator.Snapshot.Slices, item => item.Id == "B");
        Assert.True(selected.IsActive);
        Assert.Equal(14_074_000, selected.FrequencyHz);
        Assert.All(
            coordinator.Snapshot.Slices.Where(item => item.Id != "B"),
            item => Assert.False(item.IsActive));
    }

    [Fact]
    public void SelectingLiveSliceLeavesMixedAudioMuteStateUnchanged()
    {
        SliceSnapshot[] slices =
        [
            new(
                "A",
                14_100_000,
                "USB",
                300,
                3_000,
                50,
                0,
                true,
                false,
                RadioId: 3,
                IsMuted: false),
            new(
                "B",
                14_074_000,
                "DIGU",
                100,
                3_000,
                50,
                0,
                false,
                false,
                RadioId: 4,
                IsMuted: true),
            new(
                "C",
                14_200_000,
                "USB",
                300,
                3_000,
                50,
                0,
                false,
                false,
                RadioId: 5,
                IsMuted: true)
        ];

        string[] commands = RadioCoordinator.BuildActiveSliceCommands(
            slices,
            "B",
            ["slice tune 4 14.074000 autopan=0"]);

        Assert.Equal(
            [
                "slice set 4 active=1",
                "slice tune 4 14.074000 autopan=0"
            ],
            commands);
    }

    [Fact]
    public void MutingOneSliceDoesNotMuteTheOtherSlice()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"audioMute":true}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "B", values.RootElement.Clone()));

        Assert.True(result.Ok);
        Assert.False(
            Assert.Single(
                coordinator.Snapshot.Slices,
                slice => slice.Id == "A").IsMuted);
        Assert.True(
            Assert.Single(
                coordinator.Snapshot.Slices,
                slice => slice.Id == "B").IsMuted);
    }

    [Fact]
    public void DisplayControlsUpdateTheSimulatedPanadapter()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values = JsonDocument.Parse(
            """
            {
              "fftAverage": 47,
              "framesPerSecond": 18,
              "minDbm": -132,
              "wnbEnabled": true,
              "wnbLevel": 61
            }
            """);

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("pan.set", string.Empty, values.RootElement.Clone()));

        Assert.True(result.Ok);
        Assert.Equal(47, coordinator.Snapshot.Panadapter.FftAverage);
        Assert.Equal(18, coordinator.Snapshot.Panadapter.FramesPerSecond);
        Assert.Equal(-132, coordinator.Snapshot.Panadapter.MinDbm);
        Assert.True(coordinator.Snapshot.Panadapter.WnbEnabled);
        Assert.Equal(61, coordinator.Snapshot.Panadapter.WnbLevel);
    }

    [Fact]
    public void InvalidDisplayControlIsRejectedAtTheBoundary()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"framesPerSecond":60}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("pan.set", string.Empty, values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Equal(30, coordinator.Snapshot.Panadapter.FramesPerSecond);
    }

    [Fact]
    public void InvalidWidebandNoiseBlankerLevelIsRejectedAtTheBoundary()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"wnbLevel":101}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("pan.set", string.Empty, values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Equal(50, coordinator.Snapshot.Panadapter.WnbLevel);
    }

    [Fact]
    public void InvalidReceiveControlIsRejectedAtTheBoundary()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"rxAntenna":"ANT1 mode=FM"}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "A", values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Contains(
            "antenna",
            result.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ANT1", coordinator.Snapshot.Slices[0].RxAntenna);
    }

    [Fact]
    public void ActiveSliceCannotBeClearedWithoutSelectingAnother()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        using JsonDocument values =
            JsonDocument.Parse("""{"isActive":false}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "B", values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Contains(
            "true",
            result.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("A", coordinator.Snapshot.ActiveSliceId);
    }

    [Fact]
    public void LiveReceiveBridgeRejectsRadioControlIntents()
    {
        RadioCoordinator coordinator = CreateCoordinator(mode: "FlexRx");
        using JsonDocument values =
            JsonDocument.Parse("""{"frequencyHz":14274000}""");

        IntentResult result = coordinator.ApplyIntent(
            new ControlIntent("slice.set", "A", values.RootElement.Clone()));

        Assert.False(result.Ok);
        Assert.Contains("receive-only", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(coordinator.Snapshot.CanTransmit);
    }

    [Fact]
    public void WelcomeCanBeQueuedBeforePresenceAnnouncement()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        ClaimsPrincipal principal = new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "operator-a"),
                    new Claim(ClaimTypes.Name, "Operator A")
                ],
                "test"));
        RadioClientConnection connection = coordinator.Register(principal);
        Assert.False(connection.Outbox.TryRead(out _));

        coordinator.SendJson(connection, new { type = "welcome" });
        coordinator.NotifyPresenceChanged();

        Assert.True(connection.Outbox.TryRead(out OutboundMessage? first));
        using JsonDocument welcome = JsonDocument.Parse(first.Payload);
        Assert.Equal(
            "welcome",
            welcome.RootElement.GetProperty("type").GetString());

        Assert.True(connection.Outbox.TryRead(out OutboundMessage? second));
        using JsonDocument presence = JsonDocument.Parse(second.Payload);
        Assert.Equal(
            "presence",
            presence.RootElement.GetProperty("event").GetString());
        coordinator.Unregister(connection.ClientId);
    }

    [Fact]
    public void AudioFramesAreNotBroadcastWithoutLiveSlices()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        ClaimsPrincipal principal = new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "operator-a"),
                    new Claim(ClaimTypes.Name, "Operator A")
                ],
                "test"));
        RadioClientConnection connection = coordinator.Register(principal);
        Drain(connection);

        coordinator.SetLiveSlices([]);
        Drain(connection);
        coordinator.BroadcastAudio(new byte[] { 1, 2, 3, 4 });

        Assert.False(connection.Outbox.TryRead(out _));

        coordinator.SetLiveSlices(
        [
            new SliceSnapshot(
                "A",
                7_074_000,
                "DIGU",
                100,
                3_000,
                50,
                0,
                true,
                false,
                RadioId: 3)
        ]);
        Drain(connection);
        coordinator.BroadcastAudio(new byte[] { 1, 2, 3, 4 });

        Assert.True(connection.Outbox.TryRead(out OutboundMessage? message));
        Assert.Equal(WebSocketMessageType.Binary, message.MessageType);
        coordinator.Unregister(connection.ClientId);
    }

    [Fact]
    public void HiddenBrowserClientsReceiveStateButNotRealtimeFrames()
    {
        RadioCoordinator coordinator = CreateCoordinator();
        ClaimsPrincipal principal = new(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "operator-a"),
                    new Claim(ClaimTypes.Name, "Operator A")
                ],
                "test"));
        RadioClientConnection connection = coordinator.Register(principal);
        coordinator.SetLiveSlices(
        [
            new SliceSnapshot(
                "A",
                7_074_000,
                "DIGU",
                100,
                3_000,
                50,
                0,
                true,
                false,
                RadioId: 3)
        ]);
        Drain(connection);

        Assert.True(
            connection.TryEnqueue(
                new OutboundMessage(
                    WebSocketMessageType.Binary,
                    new byte[] { 9, 9, 9, 9 })));
        connection.SetPageVisible(false);
        coordinator.BroadcastSpectrum(new byte[] { 1, 2, 3, 4 });
        coordinator.BroadcastAudio(new byte[] { 5, 6, 7, 8 });
        coordinator.SendJson(connection, new { type = "state" });

        Assert.True(connection.Outbox.TryRead(out OutboundMessage? stale));
        Assert.False(connection.ShouldDeliver(stale));
        Assert.True(connection.Outbox.TryRead(out OutboundMessage? state));
        Assert.Equal(WebSocketMessageType.Text, state.MessageType);
        Assert.True(connection.ShouldDeliver(state));
        Assert.False(connection.Outbox.TryRead(out _));

        connection.SetPageVisible(true);
        coordinator.BroadcastSpectrum(new byte[] { 1, 2, 3, 4 });
        coordinator.BroadcastAudio(new byte[] { 5, 6, 7, 8 });

        Assert.True(connection.Outbox.TryRead(out OutboundMessage? spectrum));
        Assert.Equal(WebSocketMessageType.Binary, spectrum.MessageType);
        Assert.True(connection.Outbox.TryRead(out OutboundMessage? audio));
        Assert.Equal(WebSocketMessageType.Binary, audio.MessageType);
        coordinator.Unregister(connection.ClientId);
    }

    [Fact]
    public void RadioAdmissionStateComesFromTheLiveConnectionAttempt()
    {
        RadioCoordinator coordinator = CreateCoordinator(mode: "FlexRx");

        coordinator.SetRadioConnection(
            false,
            connectionState: "radio-busy",
            connectionError: "The radio rejected another GUI client.");

        Assert.False(coordinator.Snapshot.Connected);
        Assert.Equal("radio-busy", coordinator.Snapshot.ConnectionState);
        Assert.Equal(
            "The radio rejected another GUI client.",
            coordinator.Snapshot.ConnectionError);

        coordinator.SetRadioConnection(true, "FLEX-6700", "1234-5678");

        Assert.True(coordinator.Snapshot.Connected);
        Assert.Equal("connected", coordinator.Snapshot.ConnectionState);
        Assert.Null(coordinator.Snapshot.ConnectionError);
    }

    [Fact]
    public void BrowserQueueDiagnosticsProveOldFramesAreDroppedUnderBackpressure()
    {
        RadioClientConnection connection = new(
            "client-a",
            "operator-a",
            "Operator A",
            [AetherSDR.Web.Auth.AetherRoles.Control]);

        for (int index = 0; index < 100; index++)
        {
            Assert.True(
                connection.TryEnqueue(
                    new OutboundMessage(
                        WebSocketMessageType.Binary,
                        new byte[] { checked((byte)index) })));
        }

        RadioClientQueueDiagnostics saturated = connection.GetDiagnostics();
        Assert.Equal(RadioClientConnection.QueueCapacity, saturated.QueueDepth);
        Assert.Equal(100, saturated.EnqueuedMessages);
        Assert.Equal(36, saturated.DroppedMessages);
        Assert.Null(saturated.Audio);

        List<byte> retained = [];
        while (connection.Outbox.TryRead(out OutboundMessage? message))
        {
            connection.MarkDequeued();
            retained.Add(message.Payload.Span[0]);
        }

        Assert.Equal(64, retained.Count);
        Assert.Equal(36, retained[0]);
        Assert.Equal(99, retained[^1]);
        Assert.Equal(0, connection.GetDiagnostics().QueueDepth);
    }

    [Fact]
    public void BrowserAudioDiagnosticsStayWithTheirWebSocketConnection()
    {
        RadioClientConnection connection = new(
            "client-a",
            "operator-a",
            "Operator A",
            [AetherSDR.Web.Auth.AetherRoles.Control]);
        DateTimeOffset reportedAt = DateTimeOffset.UtcNow;
        RadioBrowserAudioDiagnostics audio = new(
            true,
            "running",
            "worker",
            true,
            false,
            false,
            1,
            1,
            true,
            "A",
            24_000,
            48_000,
            12,
            3_072,
            0,
            0,
            12,
            2_880,
            192,
            8,
            true,
            0,
            0,
            128,
            5,
            10,
            23,
            100,
            reportedAt);

        connection.UpdateAudioDiagnostics(audio);

        RadioBrowserAudioDiagnostics? snapshot =
            connection.GetDiagnostics().Audio;
        Assert.NotNull(snapshot);
        Assert.Equal("A", snapshot.ActiveSliceId);
        Assert.Equal(23, snapshot.EstimatedLatencyMilliseconds);
        Assert.Equal(reportedAt, snapshot.ReportedAt);
    }

    private static void Drain(RadioClientConnection connection)
    {
        while (connection.Outbox.TryRead(out _))
        {
        }
    }

    private static RadioCoordinator CreateCoordinator(
        bool allowTransmit = false,
        string mode = "Simulation")
    {
        return new RadioCoordinator(
            NullLogger<RadioCoordinator>.Instance,
            Options.Create(
                new RadioSettings
                {
                    Mode = mode,
                    AllowTransmit = allowTransmit,
                    SessionId = "test-radio"
                }),
            new TxLeaseManager());
    }
}
