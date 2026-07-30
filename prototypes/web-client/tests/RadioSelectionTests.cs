using System.Net;
using System.Text;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RadioSelectionTests
{
    [Fact]
    public void DiscoveryParserValidatesAndNormalizesRadioIdentity()
    {
        byte[] packet = Encoding.UTF8.GetBytes(
            "name=FLEX-6700 model=FLEX-6700 serial=1234-5678 " +
            "nickname=Main\u007fStation callsign=K1ABC ip=192.168.7.10 " +
            "port=4992 status=Available version=4.2.18 inuse=0 mf_enable=1 " +
            "available_clients=2 licensed_clients=2");

        DiscoveredFlexRadio? radio = FlexDiscoveryParser.TryParse(
            packet,
            IPAddress.Parse("192.168.7.99"));

        Assert.NotNull(radio);
        Assert.Equal("flex:1234-5678", radio.RadioId);
        Assert.Equal("Main Station", radio.Nickname);
        Assert.Equal("192.168.7.10", radio.Host);
        Assert.Equal(4992, radio.Port);
        Assert.True(radio.MultiFlexEnabled);
        Assert.Equal(2, radio.AvailableClients);
        Assert.Equal(2, radio.LicensedClients);
    }

    [Fact]
    public void DiscoveryParserRejectsMissingIdentityAndInvalidPorts()
    {
        Assert.Null(
            FlexDiscoveryParser.TryParse(
                Encoding.UTF8.GetBytes("model=FLEX-6700 ip=192.168.7.10"),
                IPAddress.Loopback));
        Assert.Null(
            FlexDiscoveryParser.TryParse(
                Encoding.UTF8.GetBytes(
                    "model=FLEX-6700 serial=1234 ip=192.168.7.10 port=70000"),
                IPAddress.Loopback));
    }

    [Fact]
    public async Task SelectingDiscoveredRadioSignalsSessionHandoff()
    {
        RadioSelectionManager manager = new(
            Options.Create(
                new RadioSettings
                {
                    Host = "127.77.45.252",
                    TcpPort = 4992
                }));
        SelectedRadioEndpoint configured = manager.Selected;
        manager.Upsert(
            new DiscoveredFlexRadio(
                "flex:RADIO-B",
                "FLEX-6600",
                "FLEX-6600",
                "RADIO-B",
                "Backup",
                "K1ABC",
                "192.168.7.20",
                4992,
                "Available",
                "4.2.18",
                false,
                true,
                DateTimeOffset.UtcNow));
        Task changed = manager.WaitForChangeAsync(
            configured.Revision,
            CancellationToken.None);

        bool accepted = manager.TrySelect(
            "flex:RADIO-B",
            out SelectedRadioEndpoint selected,
            out bool connectionChanged,
            out string? error);

        Assert.True(accepted);
        Assert.True(connectionChanged);
        Assert.Null(error);
        Assert.Equal("192.168.7.20", selected.Host);
        await changed.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task LowBandwidthChangeSignalsSessionReconnect()
    {
        RadioSelectionManager manager = new(
            Options.Create(
                new RadioSettings
                {
                    Host = "127.77.45.252",
                    TcpPort = 4992
                }));
        SelectedRadioEndpoint configured = manager.Selected;
        Task changed = manager.WaitForChangeAsync(
            configured.Revision,
            CancellationToken.None);

        bool reconnecting = manager.SetLowBandwidth(true);

        Assert.True(reconnecting);
        Assert.True(manager.LowBandwidth);
        Assert.True(manager.GetSnapshot().LowBandwidth);
        await changed.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void SessionProfileRestoresOnlyFpsObservedBeforeLowBandwidth()
    {
        SessionRadioSelection selection = new(
            new SelectedRadioEndpoint(
                "flex:RADIO-A",
                "192.168.7.10",
                4992,
                1),
            lowBandwidth: false);
        PanadapterSnapshot pan = new(
            CenterFrequencyHz: 14_100_000,
            BandwidthHz: 200_000,
            MinDbm: -130,
            MaxDbm: -40,
            FramesPerSecond: 17,
            Id: "0x40000002",
            StreamId: 0x40000002);

        Assert.True(selection.SetLowBandwidth(true, [pan]));
        Assert.Empty(selection.NormalDisplayRatesToRestore);
        Assert.True(selection.SetLowBandwidth(false));
        Assert.Equal(
            17,
            selection.NormalDisplayRatesToRestore["0x40000002"]);

        selection.MarkNormalDisplayRatesRestored();
        Assert.Empty(selection.NormalDisplayRatesToRestore);
    }

    [Fact]
    public void ArbitraryHostCannotBeSelected()
    {
        RadioSelectionManager manager = new(
            Options.Create(
                new RadioSettings
                {
                    Host = "127.77.45.252",
                    TcpPort = 4992
                }));

        bool accepted = manager.TrySelect(
            "http://untrusted.example",
            out _,
            out _,
            out string? error);

        Assert.False(accepted);
        Assert.Contains("not discovered", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InUseRadioIsOfferedSoLiveGuiRegistrationCanDecide()
    {
        RadioSelectionManager manager = new(
            Options.Create(
                new RadioSettings
                {
                    Host = "127.77.45.252",
                    TcpPort = 4992
                }));
        manager.Upsert(
            new DiscoveredFlexRadio(
                "flex:BUSY",
                "FLEX-6400",
                "FLEX-6400",
                "BUSY",
                "Busy",
                "K1ABC",
                "192.168.7.30",
                4992,
                "In_Use",
                "4.2.18",
                true,
                false,
                DateTimeOffset.UtcNow));

        bool accepted = manager.TrySelect(
            "flex:BUSY",
            out SelectedRadioEndpoint selected,
            out _,
            out string? error);

        Assert.True(accepted);
        Assert.Null(error);
        Assert.Equal("flex:BUSY", selected.RadioId);
        Assert.True(
            manager.GetSnapshot().Radios
                .Single(option => option.RadioId == "flex:BUSY")
                .CanSelect);
    }

    [Fact]
    public void DiscoveryCapacityIsAHintAndLiveRadioRemainsAuthoritative()
    {
        byte[] packet = Encoding.UTF8.GetBytes(
            "model=FLEX-6700 serial=FULL ip=192.168.7.31 port=4992 " +
            "status=In_Use inuse=1 available_clients=0 licensed_clients=1");
        DiscoveredFlexRadio? radio = FlexDiscoveryParser.TryParse(
            packet,
            IPAddress.Parse("192.168.7.31"));
        Assert.NotNull(radio);
        Assert.False(radio.MultiFlexEnabled);
        Assert.Equal(0, radio.AvailableClients);

        RadioSelectionManager manager = new(
            Options.Create(
                new RadioSettings
                {
                    Host = "127.77.45.252",
                    TcpPort = 4992
                }));
        manager.Upsert(radio);

        bool accepted = manager.TryResolve(
            radio.RadioId,
            out SelectedRadioEndpoint selected,
            out string? error);

        Assert.True(accepted);
        Assert.Null(error);
        Assert.Equal(radio.RadioId, selected.RadioId);
        RadioSelectionOption option = manager.GetSnapshot().Radios
            .Single(candidate => candidate.RadioId == radio.RadioId);
        Assert.True(option.CanSelect);
        Assert.Equal(0, option.AvailableClients);
    }
}
