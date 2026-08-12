using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class AetherRemoteBootstrapTests
{
    [Fact]
    public void InstallCommandPinsInstallerAndReleaseKeyWithoutEnrollmentSecret()
    {
        string installerSha = new('a', 64);
        string releaseKeySha = new('b', 64);
        string enrollmentCode = new('c', 64);
        AetherRemoteBootstrapDocument document = Document(
            installerSha,
            releaseKeySha);

        string command = AetherRemoteBootstrapService.BuildInstallCommand(
            document,
            "https://radio.example.org",
            "station-east");

        Assert.Contains(document.InstallerUrl, command, StringComparison.Ordinal);
        Assert.Contains(installerSha, command, StringComparison.Ordinal);
        Assert.Contains(releaseKeySha, command, StringComparison.Ordinal);
        Assert.Contains("--station-id 'station-east'", command, StringComparison.Ordinal);
        Assert.Contains("--gateway 'https://radio.example.org'", command, StringComparison.Ordinal);
        Assert.DoesNotContain(enrollmentCode, command, StringComparison.Ordinal);
        Assert.DoesNotContain("enrollment", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallCommandRejectsNonCanonicalStationOrGateway()
    {
        AetherRemoteBootstrapDocument document = Document(
            new string('a', 64),
            new string('b', 64));

        Assert.Throws<ArgumentException>(() =>
            AetherRemoteBootstrapService.BuildInstallCommand(
                document,
                "https://radio.example.org",
                "../station"));
        Assert.Throws<InvalidOperationException>(() =>
            AetherRemoteBootstrapService.BuildInstallCommand(
                document,
                "http://radio.example.org",
                "station-east"));
    }

    private static AetherRemoteBootstrapDocument Document(
        string installerSha,
        string releaseKeySha) =>
        new(
            SchemaVersion: 1,
            GatewayVersion: "8.5.0",
            ReleaseIdentity: "aethersdr-8.5.0-beta.1",
            ReleaseVersion: "8.5.0-beta.1",
            MinimumCompatibleAgentVersion: "8.5.0-beta.1",
            MaximumCompatibleAgentVersion: "8.5.0-beta.1",
            MinimumStationProtocolVersion: 2,
            MaximumStationProtocolVersion: 3,
            BrokerWebSocketUrl:
                "wss://radio.example.org/aetherremote/broker/station/v1",
            BrokerTokenUrl:
                "https://radio.example.org/aetherremote/broker/station/v1/token",
            EnrollmentUrl:
                "https://radio.example.org/api/station-enrollment/redeem",
            InstallerUrl:
                "https://radio.example.org/aetherremote/install",
            InstallerSha256: installerSha,
            ReleaseVerificationKey: new(
                "release-key",
                "ecdsa-p256-sha256",
                releaseKeySha,
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="),
            Architectures:
            [
                new(
                    "linux-x64",
                    "https://radio.example.org/aetherremote/releases/aethersdr-8.5.0-beta.1/linux-x64/manifest",
                    "https://radio.example.org/aetherremote/releases/aethersdr-8.5.0-beta.1/linux-x64/agent",
                    "https://radio.example.org/aetherremote/releases/aethersdr-8.5.0-beta.1/linux-x64/station-engine")
            ]);
}
