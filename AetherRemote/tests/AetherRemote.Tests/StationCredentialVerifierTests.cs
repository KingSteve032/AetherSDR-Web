using AetherRemote.Broker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherRemote.Tests;

public sealed class StationCredentialVerifierTests
{
    private const string StationCredential =
        "station-credential-with-more-than-thirty-two-characters";
    private const string RuntimeCredential =
        "runtime-credential-with-more-than-thirty-two-characters";
    private const string AdministrationCredential =
        "administration-credential-with-more-than-thirty-two-characters";

    [Fact]
    public void AcceptsOnlyMatchingStationCredential()
    {
        StationCredentialVerifier verifier = CreateVerifier();

        Assert.True(
            verifier.VerifyStation("station-one", StationCredential));
        Assert.False(
            verifier.VerifyStation("station-one", RuntimeCredential));
        Assert.False(
            verifier.VerifyStation("station-one", AdministrationCredential));
        Assert.False(
            verifier.VerifyStation("unknown-station", StationCredential));
    }

    [Fact]
    public void RuntimeAndAdministrationCredentialsAreSeparated()
    {
        StationCredentialVerifier verifier = CreateVerifier();

        Assert.True(verifier.VerifyRuntime(RuntimeCredential));
        Assert.False(verifier.VerifyRuntime(AdministrationCredential));
        Assert.False(verifier.VerifyRuntime(StationCredential));

        Assert.True(
            verifier.VerifyAdministration(AdministrationCredential));
        Assert.False(verifier.VerifyAdministration(RuntimeCredential));
        Assert.False(verifier.VerifyAdministration(StationCredential));
    }

    [Fact]
    public void IdenticalRuntimeAndAdministrationCredentialsFailClosed()
    {
        StationLinkSettings settings = CreateSettings();
        settings.AdministrationCredentialSha256 =
            settings.RuntimeCredentialSha256;

        Assert.Throws<InvalidOperationException>(
            () => CreateVerifier(settings));
    }

    [Fact]
    public void MissingRequiredCredentialVerifierFailsClosed()
    {
        StationLinkSettings settings = CreateSettings();
        settings.AdministrationCredentialSha256 = string.Empty;

        Assert.Throws<InvalidOperationException>(
            () => CreateVerifier(settings));
    }

    [Fact]
    public void DuplicateStationIdsFailClosed()
    {
        StationCredentialSettings duplicate = new()
        {
            StationId = "station-one",
            CredentialSha256 =
                StationCredentialVerifier.HashCredential(StationCredential)
        };
        StationLinkSettings settings = CreateSettings();
        settings.Stations = [duplicate, duplicate];

        Assert.Throws<InvalidOperationException>(
            () => CreateVerifier(settings));
    }

    private static StationCredentialVerifier CreateVerifier() =>
        CreateVerifier(CreateSettings());

    private static StationLinkSettings CreateSettings() =>
        new()
        {
            Enabled = true,
            RuntimeCredentialSha256 =
                StationCredentialVerifier.HashCredential(RuntimeCredential),
            AdministrationCredentialSha256 =
                StationCredentialVerifier.HashCredential(
                    AdministrationCredential),
            Stations =
            [
                new StationCredentialSettings
                {
                    StationId = "station-one",
                    CredentialSha256 =
                        StationCredentialVerifier.HashCredential(
                            StationCredential)
                }
            ]
        };

    private static StationCredentialVerifier CreateVerifier(
        StationLinkSettings settings)
    {
        IOptions<StationLinkSettings> options = Options.Create(settings);
        StationEnrollmentRegistry enrollments = new(
            options,
            NullLogger<StationEnrollmentRegistry>.Instance);
        return new StationCredentialVerifier(options, enrollments);
    }
}
