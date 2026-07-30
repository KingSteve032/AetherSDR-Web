using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class FlexStatusParserTests
{
    [Fact]
    public void SliceStatusPreservesMultiWordObjectAndNativeKeys()
    {
        const string line =
            "S1A2B3C4D|slice 3 client_handle=0x1a2b3c4d in_use=1 " +
            "RF_frequency=14.263000 mode=USB filter_lo=300 filter_hi=3000 " +
            "audio_level=55 audio_mute=0 index_letter=C";

        Assert.True(
            FlexStatusParser.TryParseSliceStatus(
                line,
                out int radioId,
                out IReadOnlyDictionary<string, string> fields));
        Assert.Equal(3, radioId);
        Assert.Equal("14.263000", fields["RF_frequency"]);
        Assert.Equal("55", fields["audio_level"]);
        Assert.True(
            FlexStatusParser.TryParseFlexUInt(
                fields["client_handle"],
                out uint owner));
        Assert.Equal((uint)0x1A2B3C4D, owner);
    }

    [Fact]
    public void NonSliceStatusIsIgnored()
    {
        Assert.False(
            FlexStatusParser.TryParseSliceStatus(
                "S1|display pan 0x40000000 center=14.280000",
                out _,
                out _));
    }

    [Fact]
    public void PanStatusParsesRadioAuthoritativeCenterAndBandwidth()
    {
        const string line =
            "S1A2B3C4D|display pan 0x40000001 center=14.050000 " +
            "bandwidth=0.200000 client_handle=0x1a2b3c4d";

        Assert.True(
            FlexStatusParser.TryParsePanStatus(
                line,
                out uint panId,
                out IReadOnlyDictionary<string, string> fields));
        Assert.Equal((uint)0x40000001, panId);
        Assert.Equal("14.050000", fields["center"]);
        Assert.Equal("0.200000", fields["bandwidth"]);
    }

    [Fact]
    public void RestoredPanTrackerAcceptsOnlyTheCurrentGuiClient()
    {
        const uint clientHandle = 0x1A2B3C4D;
        FlexRestoredPanTracker tracker = new(clientHandle);

        tracker.Observe(
            "S1|display pan 0x40000001 center=14.050000 " +
            "client_handle=0x7594c952");
        tracker.Observe(
            "S1|display pan 0x40000002 center=14.100000 " +
            "bandwidth=0.200000 client_handle=0x1a2b3c4d");

        FlexRestoredPanStatus restored = Assert.Single(tracker.Snapshot());
        Assert.Equal((uint)0x40000002, restored.StreamId);
        Assert.Equal("14.100000", restored.Fields["center"]);
    }

    [Fact]
    public void RestoredPanTrackerMergesPartialStatusAndRemovesInactivePan()
    {
        const uint clientHandle = 0x1A2B3C4D;
        FlexRestoredPanTracker tracker = new(clientHandle);
        tracker.Observe(
            "S1|display pan 0x40000002 center=14.100000 " +
            "client_handle=0x1a2b3c4d");
        tracker.Observe(
            "S1|display pan 0x40000002 fps=15 " +
            "client_handle=0x1a2b3c4d");

        FlexRestoredPanStatus restored = Assert.Single(tracker.Snapshot());
        Assert.Equal("14.100000", restored.Fields["center"]);
        Assert.Equal("15", restored.Fields["fps"]);

        tracker.Observe(
            "S1|display pan 0x40000002 in_use=0 " +
            "client_handle=0x1a2b3c4d");

        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void RestoredPanActivationChangesOnlyClientPixelDimensions()
    {
        PanadapterSnapshot restored = new(
            CenterFrequencyHz: 14_100_000,
            BandwidthHz: 200_000,
            MinDbm: -130,
            MaxDbm: -40,
            FramesPerSecond: 15,
            Id: "0x40000002",
            StreamId: 0x40000002);

        string command = Assert.Single(
            FlexRadioRxService.BuildRestoredPanActivationCommands(
                [restored],
                1_024,
                700));

        Assert.Equal(
            "display pan set 0x40000002 xpixels=1024 ypixels=700",
            command);
        Assert.DoesNotContain("center", command);
        Assert.DoesNotContain("bandwidth", command);
        Assert.DoesNotContain("fps", command);
    }

    [Fact]
    public void ExplicitReturnFromLowBandwidthRestoresObservedFpsOnly()
    {
        PanadapterSnapshot restored = new(
            CenterFrequencyHz: 14_100_000,
            BandwidthHz: 200_000,
            MinDbm: -130,
            MaxDbm: -40,
            FramesPerSecond: 5,
            Id: "0x40000002",
            StreamId: 0x40000002);
        Dictionary<string, int> rates =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["0x40000002"] = 17
            };

        string command = Assert.Single(
            FlexRadioRxService.BuildRestoredPanActivationCommands(
                [restored],
                1_024,
                700,
                rates));

        Assert.Equal(
            "display pan set 0x40000002 xpixels=1024 ypixels=700 fps=17",
            command);
        Assert.DoesNotContain("center", command);
        Assert.DoesNotContain("bandwidth", command);

        PanadapterSnapshot published = Assert.Single(
            FlexRadioRxService.ApplyRestoredDisplayRates(
                [restored],
                rates));
        Assert.Equal(17, published.FramesPerSecond);
        Assert.Equal(restored.CenterFrequencyHz, published.CenterFrequencyHz);
        Assert.Equal(restored.BandwidthHz, published.BandwidthHz);
    }

    [Fact]
    public void ClientStatusParsesExternalGuiIdentity()
    {
        const string line =
            "S1A2B3C4D|client 0x7594C952 connected " +
            "program=SmartSDR station=\"W4CAR Main\" " +
            "client_id=7d1f5fa9-9801-4af9-9cf3-1be01a6a2fc4 " +
            "ip=10.2.0.25 local_ptt=1";

        Assert.True(
            FlexStatusParser.TryParseClientStatus(
                line,
                out uint handle,
                out string action,
                out IReadOnlyDictionary<string, string> fields));
        Assert.Equal((uint)0x7594C952, handle);
        Assert.Equal("connected", action);
        Assert.Equal("SmartSDR", fields["program"]);
        Assert.Equal("W4CAR Main", fields["station"]);
        Assert.Equal("10.2.0.25", fields["ip"]);
    }

    [Fact]
    public void InterlockStatusParsesAuthoritativeTransmitOwner()
    {
        const string line =
            "S1A2B3C4D|interlock tx_client_handle=0x7594C952 " +
            "state=TRANSMITTING source=SW tx_allowed=1";

        Assert.True(
            FlexStatusParser.TryParseInterlockStatus(
                line,
                out IReadOnlyDictionary<string, string> fields));
        Assert.Equal("0x7594C952", fields["tx_client_handle"]);
        Assert.Equal("TRANSMITTING", fields["state"]);
        Assert.Equal("SW", fields["source"]);
    }

    [Fact]
    public void InterlockBandStatusIsNotMistakenForLiveTransmitState()
    {
        const string line =
            "S1A2B3C4D|interlock band 9 band_name=20 tx1_enabled=1";

        Assert.False(
            FlexStatusParser.TryParseInterlockStatus(
                line,
                out IReadOnlyDictionary<string, string> fields));
        Assert.Empty(fields);
    }

    [Fact]
    public void GuiClientRosterTracksConnectUpdateAndDisconnect()
    {
        FlexGuiClientRoster roster = new();
        const uint webHandle = 0x1A2B3C4D;
        const uint smartSdrHandle = 0x7594C952;

        Assert.True(roster.Observe(
            "S1|client 0x1A2B3C4D connected program=AetherSDR " +
            "station=AETHER-WEB-RX client_id=web-client ip=10.2.0.254"));
        Assert.True(roster.Observe(
            "S1|client 0x7594C952 connected program=SmartSDR " +
            "station=DESKTOP client_id=desktop-client ip=10.2.0.25"));
        Assert.True(roster.Observe(
            "S1|client 0x7594C952 local_ptt=1"));

        RadioGuiClientDiagnostics[] clients =
            roster.Snapshot(webHandle).ToArray();
        Assert.Equal(2, clients.Length);
        RadioGuiClientDiagnostics web = Assert.Single(
            clients,
            client => client.ClientHandle == webHandle);
        RadioGuiClientDiagnostics smartSdr = Assert.Single(
            clients,
            client => client.ClientHandle == smartSdrHandle);
        Assert.True(web.IsThisSession);
        Assert.False(smartSdr.IsThisSession);
        Assert.Equal("SmartSDR", smartSdr.Program);
        Assert.True(smartSdr.LocalPtt);

        Assert.True(roster.Observe(
            "S1|client 0x7594C952 disconnected forced=0"));
        Assert.Single(roster.Snapshot(webHandle));
    }

    [Fact]
    public void SliceStateMapsFirmwareDspStatusNames()
    {
        const string line =
            "S1A2B3C4D|slice 3 RF_frequency=14.074000 pan=0x40000001 " +
            "nb=1 nb_level=63 nr=1 nr_level=42 anf=1 anf_level=37 " +
            "nrl=1 lms_nr_level=61 nrs=1 speex_nr_level=58 " +
            "rnn=1 nrf=1 nrf_level=54 anfl=1 lms_anf_level=47 anft=1";

        Assert.True(
            FlexStatusParser.TryParseSliceStatus(
                line,
                out int radioId,
                out IReadOnlyDictionary<string, string> fields));
        FlexSliceState state = new(radioId);
        state.Apply(fields);
        SliceSnapshot slice = state.ToSnapshot("A");

        Assert.True(slice.Nb);
        Assert.Equal(63, slice.NbLevel);
        Assert.True(slice.Nr);
        Assert.Equal(42, slice.NrLevel);
        Assert.True(slice.Anf);
        Assert.Equal(37, slice.AnfLevel);
        Assert.True(slice.Nrl);
        Assert.Equal(61, slice.NrlLevel);
        Assert.True(slice.Nrs);
        Assert.Equal(58, slice.NrsLevel);
        Assert.True(slice.Rnn);
        Assert.True(slice.Nrf);
        Assert.Equal(54, slice.NrfLevel);
        Assert.True(slice.Anfl);
        Assert.Equal(47, slice.AnflLevel);
        Assert.True(slice.Anft);
    }

    [Fact]
    public void HiddenRestoredSlicesAreSelectedOutsideTheWebPan()
    {
        FlexSliceState restored = new(2);
        restored.Apply(
            new Dictionary<string, string>
            {
                ["RF_frequency"] = "14.100000",
                ["pan"] = "0x40000000",
                ["active"] = "0"
            });
        FlexSliceState activeRestored = new(4);
        activeRestored.Apply(
            new Dictionary<string, string>
            {
                ["RF_frequency"] = "7.195000",
                ["pan"] = "0x40000000",
                ["active"] = "1"
            });
        FlexSliceState visible = new(3);
        visible.Apply(
            new Dictionary<string, string>
            {
                ["RF_frequency"] = "7.195000",
                ["pan"] = "0x40000001"
            });

        int[] hidden = FlexRadioRxService.SelectHiddenRestoredSliceIds(
            [visible, activeRestored, restored],
            0x40000001);
        long? preferredFrequency =
            FlexRadioRxService.SelectPreferredRestoredFrequencyHz(
                [visible, activeRestored, restored],
                0x40000001);

        Assert.Equal([2, 4], hidden);
        Assert.Equal(7_195_000, preferredFrequency);
    }
}
