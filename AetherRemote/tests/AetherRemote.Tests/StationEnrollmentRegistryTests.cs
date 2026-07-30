using AetherRemote.Broker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherRemote.Tests;

public sealed class StationEnrollmentRegistryTests
{
    private const string Credential =
        "station-enrollment-credential-with-at-least-thirty-two-characters";

    [Fact]
    public void ConfiguredStationIsImportedWithoutChangingItsCredential()
    {
        using TestDirectory directory = new();
        StationEnrollmentRegistry registry = CreateRegistry(
            directory,
            configuredCredential: Credential);

        StationCredentialSnapshot station =
            Assert.Single(registry.GetSnapshot());
        Assert.Equal("station-one", station.StationId);
        Assert.Equal(StationCredentialStates.Enabled, station.State);
        Assert.Equal(StationCredentialSources.Imported, station.Source);
        Assert.True(registry.TryGetVerifier(
            "station-one",
            out byte[]? verifier));
        Assert.Equal(
            Convert.FromHexString(
                StationCredentialVerifier.HashCredential(Credential)),
            verifier);
    }

    [Fact]
    public void OneTimeCodeEnrollsAndSurvivesRegistryRestart()
    {
        using TestDirectory directory = new();
        ManualTimeProvider time = new();
        StationEnrollmentRegistry registry =
            CreateRegistry(directory, timeProvider: time);
        StationEnrollmentCodeResult code =
            registry.CreateEnrollmentCode("station-new");

        StationEnrollmentResult result = registry.Redeem(
            code.EnrollmentCode,
            StationCredentialVerifier.HashCredential(Credential));

        Assert.Equal("enroll", result.Purpose);
        Assert.Throws<StationEnrollmentException>(() => registry.Redeem(
            code.EnrollmentCode,
            StationCredentialVerifier.HashCredential(Credential)));

        StationEnrollmentRegistry restarted =
            CreateRegistry(directory, timeProvider: time);
        StationCredentialSnapshot station =
            Assert.Single(restarted.GetSnapshot());
        Assert.Equal(StationCredentialStates.Enabled, station.State);
        Assert.Equal(StationCredentialSources.Enrolled, station.Source);
        Assert.True(restarted.TryGetVerifier(
            "station-new",
            out _));
    }

    [Fact]
    public void ExpiredCodeCannotEnrollAStation()
    {
        using TestDirectory directory = new();
        ManualTimeProvider time = new();
        StationEnrollmentRegistry registry =
            CreateRegistry(directory, timeProvider: time);
        StationEnrollmentCodeResult code =
            registry.CreateEnrollmentCode("station-new");
        time.Advance(TimeSpan.FromMinutes(11));

        StationEnrollmentException exception =
            Assert.Throws<StationEnrollmentException>(() => registry.Redeem(
                code.EnrollmentCode,
                StationCredentialVerifier.HashCredential(Credential)));

        Assert.Equal("invalid_enrollment", exception.Code);
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void DisableEnableAndRevokeFailClosed()
    {
        using TestDirectory directory = new();
        StationEnrollmentRegistry registry = CreateRegistry(
            directory,
            configuredCredential: Credential);

        registry.SetState("station-one", StationCredentialStates.Disabled);
        Assert.False(registry.TryGetVerifier("station-one", out _));

        registry.SetState("station-one", StationCredentialStates.Enabled);
        Assert.True(registry.TryGetVerifier("station-one", out _));

        registry.SetState("station-one", StationCredentialStates.Revoked);
        Assert.False(registry.TryGetVerifier("station-one", out _));
        StationEnrollmentException exception =
            Assert.Throws<StationEnrollmentException>(() =>
                registry.SetState(
                    "station-one",
                    StationCredentialStates.Enabled));
        Assert.Equal("station_revoked", exception.Code);

        StationEnrollmentCodeResult code =
            registry.CreateEnrollmentCode("station-one");
        Assert.Equal("reenroll", code.Purpose);
    }

    private static StationEnrollmentRegistry CreateRegistry(
        TestDirectory directory,
        TimeProvider? timeProvider = null,
        string? configuredCredential = null)
    {
        StationLinkSettings settings = new()
        {
            EnrollmentCodeMinutes = 10,
            EnrollmentRegistryPath = directory.RegistryPath,
            Stations = configuredCredential is null
                ? []
                :
                [
                    new StationCredentialSettings
                    {
                        StationId = "station-one",
                        CredentialSha256 =
                            StationCredentialVerifier.HashCredential(
                                configuredCredential)
                    }
                ]
        };
        return new StationEnrollmentRegistry(
            Options.Create(settings),
            NullLogger<StationEnrollmentRegistry>.Instance,
            timeProvider);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset m_now =
            new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan interval)
        {
            m_now += interval;
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "aetherremote-enrollment-tests",
                Guid.NewGuid().ToString("N"));
            RegistryPath = Path.Combine(RootPath, "stations.json");
        }

        public string RootPath { get; }
        public string RegistryPath { get; }

        public void Dispose()
        {
            string testRoot = Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "aetherremote-enrollment-tests"));
            string root = Path.GetFullPath(RootPath);
            if (root.StartsWith(
                    testRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
