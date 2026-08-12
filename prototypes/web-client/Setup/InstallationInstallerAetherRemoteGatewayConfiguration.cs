using System.Security.Cryptography;
using System.Text;

namespace AetherSDR.Web.Setup;

internal sealed record InstallationInstallerAetherRemoteGatewayConfigurationPlan(
    string BrokerEnvironmentTargetPath,
    string BrokerEnvironmentMarkerPath,
    string RuntimeCredentialPath,
    string AdministrationCredentialPath,
    string EnrollmentRegistryPath);

/// <summary>
/// Owns the fixed gateway↔broker credential/configuration boundary for
/// topologies that accept remote stations. The two plaintext gateway
/// credentials are generated once and persisted owner-only; the broker receives
/// only their SHA-256 verifiers. Repair never rotates a valid credential merely
/// to converge unrelated configuration.
/// </summary>
internal static class InstallationInstallerAetherRemoteGatewayConfiguration
{
    internal const string BrokerEnvironmentTargetPath =
        "/etc/aethersdr/aetherremote/broker/environment";
    internal const string BrokerEnvironmentMarkerPath =
        "/var/lib/aethersdr-installer/aetherremote-broker-environment.sha256";
    internal const string RuntimeCredentialPath =
        "/var/lib/aethersdr/secrets/remote-stations/runtime-credential";
    internal const string AdministrationCredentialPath =
        "/var/lib/aethersdr/secrets/remote-stations/administration-credential";
    internal const string EnrollmentRegistryPath =
        "/var/lib/aethersdr/aetherremote/broker/stations.json";
    internal const string CredentialDirectory =
        "/var/lib/aethersdr/secrets/remote-stations";
    internal const int CredentialBytes = 32;

    internal static bool Required(
        InstallationInstallerUbuntuMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstallationInstallerGatewayConfiguration? configuration =
            request.GatewayConfiguration;
        return configuration is not null &&
            InstallationTopologyProfile.For(configuration.Topology)
                .AcceptsRemoteStations;
    }

    internal static InstallationInstallerAetherRemoteGatewayConfigurationPlan
        Compose(InstallationInstallerUbuntuMutationRequest request)
    {
        if (!Required(request))
        {
            throw new InvalidOperationException(
                "AetherRemote gateway configuration requires a topology that accepts remote stations.");
        }
        return new(
            BrokerEnvironmentTargetPath,
            BrokerEnvironmentMarkerPath,
            RuntimeCredentialPath,
            AdministrationCredentialPath,
            EnrollmentRegistryPath);
    }

    internal static string RenderBrokerEnvironment(
        string runtimeCredential,
        string administrationCredential)
    {
        ValidateCredential(runtimeCredential);
        ValidateCredential(administrationCredential);
        if (CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(runtimeCredential),
                Encoding.ASCII.GetBytes(administrationCredential)))
        {
            throw new InvalidOperationException(
                "AetherRemote runtime and administration credentials must be distinct.");
        }

        string runtimeSha = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.ASCII.GetBytes(runtimeCredential)));
        string administrationSha = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.ASCII.GetBytes(administrationCredential)));
        return string.Join(
            '\n',
            [
                "ASPNETCORE_URLS=http://127.0.0.1:5090",
                "StationLink__Enabled=true",
                "StationLink__RequireForwardedHttps=true",
                "StationLink__HeartbeatSeconds=10",
                "StationLink__DegradedAfterSeconds=25",
                "StationLink__DisconnectAfterSeconds=45",
                "StationLink__LinkTokenSeconds=60",
                "StationLink__EnrollmentCodeMinutes=10",
                $"StationLink__EnrollmentRegistryPath={EnrollmentRegistryPath}",
                $"StationLink__RuntimeCredentialSha256={runtimeSha}",
                $"StationLink__AdministrationCredentialSha256={administrationSha}",
                string.Empty
            ]);
    }

    internal static void ValidateCredential(string credential)
    {
        if (credential.Length != CredentialBytes * 2 ||
            !credential.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException(
                "An AetherRemote gateway credential is invalid.");
        }
    }
}
