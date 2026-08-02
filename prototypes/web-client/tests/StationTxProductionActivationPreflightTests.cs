using System.Security.Cryptography;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxProductionActivationPreflightTests
{
    private const string TargetRadioId = "FLEX:1121-1104-6700-2912";

    [Fact]
    public void FullyStagedConfigurationIsReadyWhileMasterActivationRemainsOff()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        KeyFiles files = directory.WriteKeyPair("station-key", key);
        string watchdog = directory.WriteWatchdog();
        PreflightSettings settings = CreateSettings(
            files,
            watchdog,
            activationRequested: false);

        StationTxProductionActivationPreflightReport report = Evaluate(
            directory,
            settings);

        Assert.True(report.ValidationOnly);
        Assert.False(report.WebHostStarted);
        Assert.False(report.RadioConnectionCreated);
        Assert.False(report.WatchdogProcessStarted);
        Assert.False(report.ActivationCurrentlyRequested);
        Assert.True(report.CandidateConfigurationValid);
        Assert.True(report.CandidatePlanAvailable);
        Assert.True(report.CandidateBindingApplied);
        Assert.True(report.TrustedKeyMaterialReady);
        Assert.Equal(1, report.TrustedKeyCount);
        Assert.True(report.SigningKeyMaterialReady);
        Assert.True(report.SigningKeyTrusted);
        Assert.True(report.PrimaryTransportRadioAllowed);
        Assert.True(report.EmergencyTransportRadioAllowed);
        Assert.True(report.WatchdogRadioAllowed);
        Assert.True(report.WatchdogExecutableReady);
        Assert.True(report.ReadyForOperatorActivation);
        Assert.Equal("preflight-ready", report.Reason);
        Assert.Empty(report.MissingPrerequisites);
    }

    [Fact]
    public void SigningKeyMustMatchTheTrustedPublicKey()
    {
        using TempDirectory directory = new();
        using ECDsa trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        KeyFiles trustedFiles = directory.WriteKeyPair("trusted", trusted);
        KeyFiles signingFiles = directory.WriteKeyPair("signing", signing);
        string watchdog = directory.WriteWatchdog();
        PreflightSettings settings = CreateSettings(
            new KeyFiles(
                signingFiles.PrivateKeyPath,
                trustedFiles.PublicKeyPath),
            watchdog,
            activationRequested: false);

        StationTxProductionActivationPreflightReport report = Evaluate(
            directory,
            settings);

        Assert.True(report.TrustedKeyMaterialReady);
        Assert.True(report.SigningKeyMaterialReady);
        Assert.False(report.SigningKeyTrusted);
        Assert.False(report.ReadyForOperatorActivation);
        Assert.Contains(
            "command-signing-key-not-trusted",
            report.MissingPrerequisites);
    }

    [Fact]
    public void EveryProductionTransportMustAllowTheExactTargetRadio()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        KeyFiles files = directory.WriteKeyPair("station-key", key);
        PreflightSettings settings = CreateSettings(
            files,
            directory.WriteWatchdog(),
            activationRequested: false);
        settings.CommandTransport.AllowedRadioIds = ["FLEX:OTHER-RADIO"];

        StationTxProductionActivationPreflightReport report = Evaluate(
            directory,
            settings);

        Assert.False(report.PrimaryTransportRadioAllowed);
        Assert.True(report.EmergencyTransportRadioAllowed);
        Assert.True(report.WatchdogRadioAllowed);
        Assert.False(report.ReadyForOperatorActivation);
        Assert.Contains(
            "command-transport-radio-not-allowed",
            report.MissingPrerequisites);
    }

    [Fact]
    public void ReviewedWatchdogExecutableMustExistAndBeExecutable()
    {
        using TempDirectory directory = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        KeyFiles files = directory.WriteKeyPair("station-key", key);
        string watchdog = Path.Combine(directory.Path, "AetherSDR.TxWatchdog");
        File.WriteAllText(watchdog, string.Empty);
        SetMode(
            watchdog,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite);
        PreflightSettings settings = CreateSettings(
            files,
            watchdog,
            activationRequested: false);

        StationTxProductionActivationPreflightReport report = Evaluate(
            directory,
            settings);

        Assert.False(report.WatchdogExecutableReady);
        Assert.False(report.ReadyForOperatorActivation);
        Assert.Contains(
            "watchdog-executable-unavailable",
            report.MissingPrerequisites);
    }

    private static StationTxProductionActivationPreflightReport Evaluate(
        TempDirectory directory,
        PreflightSettings settings) =>
        StationTxProductionActivationPreflight.Evaluate(
            TargetRadioId,
            directory.Path,
            settings.Activation,
            settings.Radio,
            settings.Trust,
            settings.Signing,
            settings.Coordinator,
            settings.CommandTransport,
            settings.EmergencyTransport,
            settings.Watchdog);

    private static PreflightSettings CreateSettings(
        KeyFiles files,
        string watchdogExecutable,
        bool activationRequested) =>
        new(
            new StationTxProductionActivationSettings
            {
                Enabled = activationRequested
            },
            new RadioSettings
            {
                Mode = "FlexRx",
                AllowTransmit = true,
                BrowserTxLeaseEnabled = true,
                RadioId = TargetRadioId,
                Host = "127.0.0.1",
                TcpPort = 4992
            },
            new StationTxCommandTrustSettings
            {
                VerificationEnabled = true,
                Keys =
                [
                    new StationTxCommandTrustKeySettings
                    {
                        KeyId = "station-key",
                        PublicKeyPath = files.PublicKeyPath
                    }
                ]
            },
            new StationTxCommandSigningSettings
            {
                SigningEnabled = true,
                KeyId = "station-key",
                PrivateKeyPath = files.PrivateKeyPath
            },
            new StationTxCommandEnvelopeCoordinatorSettings
            {
                SubmissionEnabled = true
            },
            new StationTxCommandTransportSettings
            {
                Enabled = true,
                AllowedRadioIds = [TargetRadioId],
                CommandTimeoutMilliseconds = 2000
            },
            new StationTxEmergencyUnkeyTransportSettings
            {
                Enabled = true,
                AllowedRadioIds = [TargetRadioId],
                CommandTimeoutMilliseconds = 2000
            },
            new IndependentTxWatchdogSettings
            {
                Enabled = true,
                ExecutablePath = watchdogExecutable,
                RequestTimeoutMilliseconds = 2000,
                RestartDelayMilliseconds = 1000,
                RadioCommandTransportEnabled = true,
                ArmingEnabled = true,
                AllowedRadioIds = [TargetRadioId],
                RadioCommandTimeoutMilliseconds = 2000
            });

    private sealed record PreflightSettings(
        StationTxProductionActivationSettings Activation,
        RadioSettings Radio,
        StationTxCommandTrustSettings Trust,
        StationTxCommandSigningSettings Signing,
        StationTxCommandEnvelopeCoordinatorSettings Coordinator,
        StationTxCommandTransportSettings CommandTransport,
        StationTxEmergencyUnkeyTransportSettings EmergencyTransport,
        IndependentTxWatchdogSettings Watchdog);

    private sealed record KeyFiles(
        string PrivateKeyPath,
        string PublicKeyPath);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-preflight-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    Path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
        }

        public string Path { get; }

        public KeyFiles WriteKeyPair(string prefix, ECDsa key)
        {
            string privatePath = System.IO.Path.Combine(
                Path,
                $"{prefix}-private.pem");
            string publicPath = System.IO.Path.Combine(
                Path,
                $"{prefix}-public.pem");
            File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
            SetMode(
                privatePath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
            SetMode(
                publicPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
            return new KeyFiles(privatePath, publicPath);
        }

        public string WriteWatchdog()
        {
            string path = System.IO.Path.Combine(
                Path,
                OperatingSystem.IsWindows()
                    ? "AetherSDR.TxWatchdog.exe"
                    : "AetherSDR.TxWatchdog");
            File.WriteAllText(path, string.Empty);
            SetMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }
}
