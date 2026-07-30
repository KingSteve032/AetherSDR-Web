using System.Text;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RemoteStationCatalogTests
{
    [Fact]
    public void RuntimeAndAdministrationCredentialFilesMustBeDistinct()
    {
        RemoteStationSettings settings = new()
        {
            BrokerUrl = "http://127.0.0.1:5090",
            RefreshSeconds = 3,
            RuntimeCredentialFile = "/tmp/shared-credential",
            AdministrationCredentialFile = "/tmp/shared-credential"
        };

        Assert.Throws<InvalidOperationException>(
            () => RemoteStationSettingsValidator.GetBrokerBaseUri(settings));
    }

    [Fact]
    public void RuntimeAndAdministrationCredentialValuesAreComparedSafely()
    {
        Assert.True(
            RemoteStationSettingsValidator.CredentialsMatch(
                "credential-value-with-at-least-thirty-two-characters",
                "credential-value-with-at-least-thirty-two-characters"));
        Assert.False(
            RemoteStationSettingsValidator.CredentialsMatch(
                "runtime-value-with-at-least-thirty-two-characters",
                "administration-value-with-at-least-thirty-two-characters"));
    }

    [Fact]
    public void ParsesBoundedRemoteStationInventory()
    {
        IReadOnlyList<RemoteRadioCatalogEntry> radios =
            RemoteStationCatalogParser.Parse(
                Encoding.UTF8.GetBytes(ValidInventory()));

        RemoteRadioCatalogEntry radio = Assert.Single(radios);
        Assert.Equal(
            "remote:odu-campus:flex:1821-1104-6601-5831",
            radio.SelectorId);
        Assert.Equal("odu-campus", radio.StationId);
        Assert.Equal("ODU-6600M", radio.Nickname);
        Assert.True(radio.StationOnline);
        Assert.True(radio.ReceiveProjectionReady);
        Assert.Equal(2, radio.AvailableClients);
        Assert.Equal(2, radio.LicensedClients);
    }

    [Fact]
    public void ParsesAdministrationHealthWithoutStationLanAddress()
    {
        RemoteStationCatalogSnapshot snapshot =
            RemoteStationCatalogParser.ParseSnapshot(
                Encoding.UTF8.GetBytes(ValidInventory()));

        RemoteStationAdministrationEntry station =
            Assert.Single(snapshot.Stations);
        Assert.Equal("odu-campus", station.StationId);
        Assert.Equal("station-instance-1", station.InstanceId);
        Assert.Equal("0.3.0", station.SoftwareVersion);
        Assert.Equal(12, station.HeartbeatSequence);
        Assert.Equal(4, station.InventorySequence);
        Assert.Equal(3, station.ConnectionCount);
        Assert.Equal("heartbeat_timeout", station.LastDisconnectReason);
        Assert.Equal(2_500, station.LastRecoveryMilliseconds);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-28T14:59:55Z"),
            station.LastDisconnectedAt);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-28T14:59:57.5Z"),
            station.LastRecoveredAt);
        Assert.Single(station.Radios);
        Assert.DoesNotContain(
            "10.3.0.15",
            System.Text.Json.JsonSerializer.Serialize(snapshot),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesBoundedRemoteReceiveSessions()
    {
        const string payload =
            """
            {
              "sessions": [
                {
                  "sessionId": "0123456789abcdef0123456789abcdef",
                  "stationId": "odu-campus",
                  "radioId": "flex:1821-1104-6601-5831",
                  "guiClientId": "b8f9310a-3247-4d48-b3ed-520b36db23e7",
                  "state": "admitted",
                  "radioModel": "FLEX-6600M",
                  "serial": "RX-ONLY",
                  "clientHandle": "7594c952",
                  "openedAt": "2026-07-28T15:00:00Z"
                }
              ]
            }
            """;

        RemoteReceiveSessionAdministrationEntry session = Assert.Single(
            RemoteReceiveSessionInventoryParser.Parse(
                Encoding.UTF8.GetBytes(payload)));

        Assert.Equal("odu-campus", session.StationId);
        Assert.Equal("7594c952", session.ClientHandle);
        Assert.Equal("admitted", session.State);
    }

    [Fact]
    public void MalformedRemoteReceiveSessionFailsClosed()
    {
        const string payload =
            """
            {
              "sessions": [
                {
                  "sessionId": "not-a-session",
                  "stationId": "odu-campus",
                  "radioId": "flex:radio",
                  "guiClientId": "b8f9310a-3247-4d48-b3ed-520b36db23e7",
                  "state": "admitted",
                  "radioModel": "FLEX-6600M",
                  "serial": "RX-ONLY",
                  "clientHandle": "7594c952",
                  "openedAt": "2026-07-28T15:00:00Z"
                }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(
            () => RemoteReceiveSessionInventoryParser.Parse(
                Encoding.UTF8.GetBytes(payload)));
    }

    [Fact]
    public void ParsesStationCredentialInventoryWithoutVerifierMaterial()
    {
        const string payload =
            """
            {
              "stations": [
                {
                  "stationId": "odu-campus",
                  "state": "enabled",
                  "source": "imported",
                  "enrolledAt": "2026-07-28T12:00:00Z",
                  "rotatedAt": null,
                  "updatedAt": "2026-07-28T12:00:00Z"
                }
              ]
            }
            """;

        RemoteStationCredentialAdministrationEntry credential =
            Assert.Single(
                RemoteStationCredentialInventoryParser.Parse(
                    Encoding.UTF8.GetBytes(payload)));

        Assert.Equal("odu-campus", credential.StationId);
        Assert.Equal("enabled", credential.State);
        Assert.DoesNotContain(
            "sha256",
            System.Text.Json.JsonSerializer.Serialize(credential),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidStationCredentialInventoryFailsClosed()
    {
        const string payload =
            """
            {
              "stations": [
                {
                  "stationId": "odu campus",
                  "state": "enabled",
                  "source": "imported",
                  "enrolledAt": "2026-07-28T12:00:00Z",
                  "rotatedAt": null,
                  "updatedAt": "2026-07-28T12:00:00Z"
                }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(
            () => RemoteStationCredentialInventoryParser.Parse(
                Encoding.UTF8.GetBytes(payload)));
    }

    [Fact]
    public void RemoteRadioAppearsWithoutExposingAStationLanAddress()
    {
        RadioSelectionManager manager = CreateManager();
        manager.ReplaceRemoteRadios(
            RemoteStationCatalogParser.Parse(
                Encoding.UTF8.GetBytes(ValidInventory())));

        RadioSelectionOption remote = manager.GetSnapshot().Radios
            .Single(radio => radio.Source == "remote");

        Assert.True(remote.Online);
        Assert.True(remote.CanSelect);
        Assert.True(remote.TunnelReady);
        Assert.Equal("odu-campus", remote.StationId);
        Assert.Equal("Via odu-campus", remote.Host);
        Assert.Equal(0, remote.Port);
        Assert.DoesNotContain("10.3.0.", remote.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteRadioResolvesOnlyToOpaqueStationProjection()
    {
        RadioSelectionManager manager = CreateManager();
        RemoteRadioCatalogEntry remote = Assert.Single(
            RemoteStationCatalogParser.Parse(
                Encoding.UTF8.GetBytes(ValidInventory())));
        manager.ReplaceRemoteRadios([remote]);

        bool accepted = manager.TryResolve(
            remote.SelectorId,
            out SelectedRadioEndpoint selected,
            out string? error);

        Assert.True(accepted);
        Assert.Null(error);
        Assert.Equal(remote.SelectorId, selected.RadioId);
        Assert.Equal("remote", selected.Source);
        Assert.Equal("odu-campus", selected.StationId);
        Assert.Equal(
            "flex:1821-1104-6601-5831",
            selected.SourceRadioId);
        Assert.Equal(0, selected.Port);
        Assert.DoesNotContain("10.3.0.", selected.Host, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedRemoteCapacityFailsClosed()
    {
        string invalid = ValidInventory()
            .Replace(
                "\"availableClients\": 2",
                "\"availableClients\": 3",
                StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(
            () => RemoteStationCatalogParser.Parse(
                Encoding.UTF8.GetBytes(invalid)));
    }

    [Fact]
    public void MalformedStationRecoveryTelemetryFailsClosed()
    {
        string invalid = ValidInventory().Replace(
            "\"lastRecoveryMilliseconds\": 2500",
            "\"lastRecoveryMilliseconds\": -1",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(
            () => RemoteStationCatalogParser.ParseSnapshot(
                Encoding.UTF8.GetBytes(invalid)));
    }

    [Fact]
    public void StationWithoutProjectionCapabilityStaysUnselectable()
    {
        string legacy = ValidInventory().Replace(
            "\"capabilities\": [\"receive-projection-v1\"],",
            "\"capabilities\": [],",
            StringComparison.Ordinal);
        RadioSelectionManager manager = CreateManager();
        manager.ReplaceRemoteRadios(
            RemoteStationCatalogParser.Parse(
                Encoding.UTF8.GetBytes(legacy)));

        RadioSelectionOption remote = manager.GetSnapshot().Radios
            .Single(radio => radio.Source == "remote");

        Assert.True(remote.Online);
        Assert.False(remote.CanSelect);
        Assert.False(remote.TunnelReady);
    }

    [Fact]
    public void CompleteRemoteSnapshotRemovesMissingRadios()
    {
        RadioSelectionManager manager = CreateManager();
        manager.ReplaceRemoteRadios(
            RemoteStationCatalogParser.Parse(
                Encoding.UTF8.GetBytes(ValidInventory())));
        Assert.Contains(
            manager.GetSnapshot().Radios,
            radio => radio.Source == "remote");

        manager.ReplaceRemoteRadios([]);

        Assert.DoesNotContain(
            manager.GetSnapshot().Radios,
            radio => radio.Source == "remote");
    }

    private static RadioSelectionManager CreateManager() =>
        new(
            Options.Create(
                new RadioSettings
                {
                    Host = "127.77.45.252",
                    TcpPort = 4992
                }));

    private static string ValidInventory() =>
        """
        {
          "stations": [
            {
              "stationId": "odu-campus",
              "instanceId": "station-instance-1",
              "state": "online",
              "softwareVersion": "0.3.0",
              "remoteAddress": "10.3.0.15",
              "connectedAt": "CONNECTED_AT",
              "lastSeen": "LAST_SEEN",
              "heartbeatSequence": 12,
              "inventorySequence": 4,
              "connectionCount": 3,
              "lastDisconnectedAt": "2026-07-28T14:59:55Z",
              "lastDisconnectReason": "heartbeat_timeout",
              "lastRecoveredAt": "2026-07-28T14:59:57.5Z",
              "lastRecoveryMilliseconds": 2500,
              "capabilities": ["receive-projection-v1"],
              "radios": [
                {
                  "radioId": "flex:1821-1104-6601-5831",
                  "model": "FLEX-6600M",
                  "serial": "1821-1104-6601-5831",
                  "nickname": "ODU-6600M",
                  "status": "available",
                  "availableClients": 2,
                  "licensedClients": 2
                }
              ]
            }
          ]
        }
        """.Replace(
            "CONNECTED_AT",
            DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
            StringComparison.Ordinal).Replace(
            "LAST_SEEN",
            DateTimeOffset.UtcNow.ToString("O"),
            StringComparison.Ordinal);
}
