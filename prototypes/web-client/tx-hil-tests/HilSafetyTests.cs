using System.Diagnostics;
using System.Text.Json;
using AetherSDR.TxHil;
using Xunit;

namespace AetherSDR.TxHil.Tests;

public sealed class HilSafetyTests
{
    [Fact]
    public void InspectDefaultsAreHardBoundToPsoc2()
    {
        HilOptions options = HilOptions.Parse(["inspect"]);

        Assert.Equal(HilCommand.Inspect, options.Command);
        Assert.Equal("10.2.0.12", options.Host);
        Assert.Equal(HilOptions.Psoc2RadioId, options.RadioId);
        Assert.Equal(HilOptions.Psoc2Serial, options.ExpectedSerial);
        Assert.Equal(
            HilOptions.MinimumFirstPulseFrequencyHz,
            options.FrequencyHz);
        Assert.Equal("USB", options.Mode);
        Assert.Equal(HilOptions.FixedFirstPulseRfPower, options.RfPower);
        Assert.Equal(
            HilOptions.FixedFirstPulseMilliseconds,
            options.KeyMilliseconds);
    }

    [Fact]
    public void RestoreIdleDefaultsIsHardBoundAndNeedsNoRfArm()
    {
        HilOptions options = HilOptions.Parse(["restore-idle-defaults"]);

        Assert.Equal(HilCommand.RestoreIdleDefaults, options.Command);
        Assert.Equal("10.2.0.12", options.Host);
        Assert.Equal(HilOptions.Psoc2RadioId, options.RadioId);
        Assert.Equal(HilOptions.Psoc2Serial, options.ExpectedSerial);
        Assert.Empty(options.ArmFile);
        Assert.Empty(options.Token);
    }

    [Fact]
    public void AnotherRadioIsRejected()
    {
        HilUsageException error = Assert.Throws<HilUsageException>(() =>
            HilOptions.Parse(
            [
                "inspect",
                "--radio-id",
                "FLEX:OTHER",
                "--expected-serial",
                "OTHER"
            ]));

        Assert.Contains("hard-bound to PSOC2", error.Message);
    }

    [Fact]
    public void PrepareRequiresExactOnAirConfirmation()
    {
        HilUsageException error = Assert.Throws<HilUsageException>(() =>
            HilOptions.Parse(
            [
                "prepare",
                "--arm-file",
                Path.Combine(Path.GetTempPath(), "arm.json"),
                "--frequency-hz",
                "14250000",
                "--on-air-confirm",
                "close enough"
            ]));

        Assert.Contains("exact on-air safety confirmation", error.Message);
    }

    [Theory]
    [InlineData("101", "--key-ms")]
    [InlineData("2", "--rf-power")]
    public void PulseLimitsCannotBeRaised(
        string value,
        string option)
    {
        HilUsageException error = Assert.Throws<HilUsageException>(() =>
            HilOptions.Parse(
            [
                "prepare",
                "--arm-file",
                Path.Combine(Path.GetTempPath(), "arm.json"),
                "--frequency-hz",
                "14250000",
                "--on-air-confirm",
                HilOptions.RequiredOnAirConfirmation,
                option,
                value
            ]));

        Assert.Contains(option[2..], error.Message);
    }

    [Theory]
    [InlineData("--mode", "AM")]
    [InlineData("--tx-antenna", "ANT2")]
    public void FirstPulseTopologyIsFixed(
        string option,
        string value)
    {
        HilUsageException error = Assert.Throws<HilUsageException>(() =>
            HilOptions.Parse(
            [
                "prepare",
                "--arm-file",
                Path.Combine(Path.GetTempPath(), "arm.json"),
                "--frequency-hz",
                "14250000",
                "--on-air-confirm",
                HilOptions.RequiredOnAirConfirmation,
                option,
                value
            ]));

        Assert.Contains(
            "fixed at USB, ANT1, 1 W, and 100 ms",
            error.Message);
    }

    [Theory]
    [InlineData("14224999")]
    [InlineData("14350001")]
    public void FirstPulseFrequencyMustBeExplicitAndInsideBoundedPhoneSegment(
        string frequency)
    {
        HilUsageException error = Assert.Throws<HilUsageException>(() =>
            HilOptions.Parse(
            [
                "prepare",
                "--arm-file",
                Path.Combine(Path.GetTempPath(), "arm.json"),
                "--frequency-hz",
                frequency,
                "--on-air-confirm",
                HilOptions.RequiredOnAirConfirmation
            ]));

        Assert.Contains("frequency-hz", error.Message);
    }

    [Fact]
    public void CompleteTransmitRouteSnapshotIsRequiredForSafeRestoration()
    {
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rfpower"] = "100",
            ["dax"] = "1",
            ["mic_selection"] = "PC",
            ["vox_enable"] = "0"
        };

        Assert.True(HilFlexSession.TryReadTransmitSettings(
            fields,
            out HilTransmitSettings? settings));
        Assert.Equal(
            new HilTransmitSettings(100, true, "PC", false),
            settings);

        fields.Remove("vox_enable");
        Assert.False(HilFlexSession.TryReadTransmitSettings(
            fields,
            out settings));
        Assert.Null(settings);
    }

    [Fact]
    public void PrepareRequiresAnExplicitClearFrequency()
    {
        HilUsageException error = Assert.Throws<HilUsageException>(() =>
            HilOptions.Parse(
            [
                "prepare",
                "--arm-file",
                Path.Combine(Path.GetTempPath(), "arm.json"),
                "--on-air-confirm",
                HilOptions.RequiredOnAirConfirmation
            ]));

        Assert.Contains("frequency-hz is required", error.Message);
    }

    [Fact]
    public async Task ArmManifestStoresOnlyTokenHashAndIsConsumedOnce()
    {
        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(temporary.Path, "arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        HilOptions options = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) =
            HilArmManifest.Create(options, time);

        await HilArmManifest.WriteAsync(
            armFile,
            manifest,
            CancellationToken.None);

        string stored = await File.ReadAllTextAsync(armFile);
        Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
        Assert.Contains(manifest.TokenSha256, stored, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(armFile));
        }

        HilArmManifest consumed = await HilArmManifest.ConsumeAsync(
            armFile,
            token,
            time,
            CancellationToken.None);

        Assert.Equal(manifest, consumed);
        Assert.False(File.Exists(armFile));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            HilArmManifest.ConsumeAsync(
                armFile,
                token,
                time,
                CancellationToken.None));
    }

    [Fact]
    public async Task WrongTokenDoesNotConsumeManifest()
    {
        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(temporary.Path, "arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        (HilArmManifest manifest, string token) =
            HilArmManifest.Create(PrepareOptions(armFile), time);
        await HilArmManifest.WriteAsync(
            armFile,
            manifest,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilArmManifest.ConsumeAsync(
                armFile,
                new string('0', token.Length),
                time,
                CancellationToken.None));

        Assert.True(File.Exists(armFile));
    }

    [Fact]
    public async Task ExpiredManifestFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(temporary.Path, "arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        (HilArmManifest manifest, string token) =
            HilArmManifest.Create(PrepareOptions(armFile), time);
        await HilArmManifest.WriteAsync(
            armFile,
            manifest,
            CancellationToken.None);
        time.Advance(HilOptions.ArmLifetime);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilArmManifest.ConsumeAsync(
                armFile,
                token,
                time,
                CancellationToken.None));

        Assert.True(File.Exists(armFile));
    }

    [Fact]
    public async Task PermissiveArmFileModeIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(temporary.Path, "arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        (HilArmManifest manifest, string token) =
            HilArmManifest.Create(PrepareOptions(armFile), time);
        await File.WriteAllTextAsync(
            armFile,
            JsonSerializer.Serialize(manifest));
        File.SetUnixFileMode(
            armFile,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HilArmManifest.ConsumeAsync(
                    armFile,
                    token,
                    time,
                    CancellationToken.None));

        Assert.Contains("exact mode 0600", error.Message);
    }

    [Fact]
    public void SafetyExpiryCommandsUseTheSameFixedOnAirBounds()
    {
        string armFile = Path.Combine(Path.GetTempPath(), "safety-arm.json");
        HilOptions prepare = HilOptions.Parse(
        [
            "prepare-safety-expiry",
            "--arm-file",
            armFile,
            "--frequency-hz",
            "14250000",
            "--on-air-confirm",
            HilOptions.RequiredOnAirConfirmation
        ]);
        HilOptions run = HilOptions.Parse(
        [
            "safety-expiry",
            "--arm-file",
            armFile,
            "--token",
            "test-token"
        ]);

        Assert.Equal(HilCommand.PrepareSafetyExpiry, prepare.Command);
        Assert.Equal(HilCommand.SafetyExpiry, run.Command);
        Assert.Equal(14_250_000, prepare.FrequencyHz);
        Assert.Equal("ANT1", prepare.TxAntenna);
        Assert.Equal(1, prepare.RfPower);
        Assert.Equal(100, prepare.KeyMilliseconds);
    }

    [Fact]
    public async Task ManifestPurposeCannotCrossLaunchArmedOperations()
    {
        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(temporary.Path, "safety-arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            time,
            HilArmManifest.SafetyExpiryPurpose);
        await HilArmManifest.WriteAsync(
            armFile,
            manifest,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilArmManifest.ConsumeAsync(
                armFile,
                token,
                time,
                CancellationToken.None));
        Assert.True(File.Exists(armFile));

        HilArmManifest consumed = await HilArmManifest.ConsumeAsync(
            armFile,
            token,
            HilArmManifest.SafetyExpiryPurpose,
            time,
            CancellationToken.None);
        Assert.Equal(HilArmManifest.SafetyExpiryPurpose, consumed.Purpose);
        Assert.False(File.Exists(armFile));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToPulseOptions(
                consumed,
                armFile,
                token));
    }

    [Fact]
    public void SafetyExpiryOptionsComeOnlyFromConsumedManifest()
    {
        string armFile = Path.Combine(Path.GetTempPath(), "safety-arm.json");
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            purpose: HilArmManifest.SafetyExpiryPurpose);

        HilOptions safety = HilArmManifest.ToSafetyExpiryOptions(
            manifest,
            armFile,
            token);

        Assert.Equal(HilCommand.SafetyExpiry, safety.Command);
        Assert.Equal(14_250_000, safety.FrequencyHz);
        Assert.Equal("ANT1", safety.TxAntenna);
        Assert.Equal(1, safety.RfPower);
        Assert.Equal(100, safety.KeyMilliseconds);
    }

    [Fact]
    public void SafetySessionLossCommandsUseTheSameFixedOnAirBounds()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "session-loss-arm.json");
        HilOptions prepare = HilOptions.Parse(
        [
            "prepare-safety-session-loss",
            "--arm-file",
            armFile,
            "--frequency-hz",
            "14250000",
            "--on-air-confirm",
            HilOptions.RequiredOnAirConfirmation
        ]);
        HilOptions run = HilOptions.Parse(
        [
            "safety-session-loss",
            "--arm-file",
            armFile,
            "--token",
            "test-token"
        ]);

        Assert.Equal(HilCommand.PrepareSafetySessionLoss, prepare.Command);
        Assert.Equal(HilCommand.SafetySessionLoss, run.Command);
        Assert.Equal(14_250_000, prepare.FrequencyHz);
        Assert.Equal("ANT1", prepare.TxAntenna);
        Assert.Equal(1, prepare.RfPower);
        Assert.Equal(100, prepare.KeyMilliseconds);
    }

    [Fact]
    public async Task SessionLossManifestCannotLaunchAnotherArmedOperation()
    {
        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(
            temporary.Path,
            "session-loss-arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            time,
            HilArmManifest.SafetySessionLossPurpose);
        await HilArmManifest.WriteAsync(
            armFile,
            manifest,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilArmManifest.ConsumeAsync(
                armFile,
                token,
                HilArmManifest.SafetyExpiryPurpose,
                time,
                CancellationToken.None));
        Assert.True(File.Exists(armFile));

        HilArmManifest consumed = await HilArmManifest.ConsumeAsync(
            armFile,
            token,
            HilArmManifest.SafetySessionLossPurpose,
            time,
            CancellationToken.None);
        Assert.False(File.Exists(armFile));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToPulseOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyExpiryOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyAuthenticationLossOptions(
                consumed,
                armFile,
                token));
    }

    [Fact]
    public void SafetySessionLossOptionsComeOnlyFromConsumedManifest()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "session-loss-arm.json");
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            purpose: HilArmManifest.SafetySessionLossPurpose);

        HilOptions safety = HilArmManifest.ToSafetySessionLossOptions(
            manifest,
            armFile,
            token);

        Assert.Equal(HilCommand.SafetySessionLoss, safety.Command);
        Assert.Equal(14_250_000, safety.FrequencyHz);
        Assert.Equal("ANT1", safety.TxAntenna);
        Assert.Equal(1, safety.RfPower);
        Assert.Equal(100, safety.KeyMilliseconds);
    }

    [Fact]
    public void SafetyAuthenticationLossCommandsUseTheSameFixedOnAirBounds()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "authentication-loss-arm.json");
        HilOptions prepare = HilOptions.Parse(
        [
            "prepare-safety-auth-loss",
            "--arm-file",
            armFile,
            "--frequency-hz",
            "14250000",
            "--on-air-confirm",
            HilOptions.RequiredOnAirConfirmation
        ]);
        HilOptions run = HilOptions.Parse(
        [
            "safety-auth-loss",
            "--arm-file",
            armFile,
            "--token",
            "test-token"
        ]);

        Assert.Equal(
            HilCommand.PrepareSafetyAuthenticationLoss,
            prepare.Command);
        Assert.Equal(HilCommand.SafetyAuthenticationLoss, run.Command);
        Assert.Equal(14_250_000, prepare.FrequencyHz);
        Assert.Equal("ANT1", prepare.TxAntenna);
        Assert.Equal(1, prepare.RfPower);
        Assert.Equal(100, prepare.KeyMilliseconds);
    }

    [Fact]
    public async Task AuthenticationLossManifestCannotLaunchAnotherOperation()
    {
        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(
            temporary.Path,
            "authentication-loss-arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 20, 0, 0, TimeSpan.Zero));
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            time,
            HilArmManifest.SafetyAuthenticationLossPurpose);
        await HilArmManifest.WriteAsync(
            armFile,
            manifest,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilArmManifest.ConsumeAsync(
                armFile,
                token,
                HilArmManifest.SafetySessionLossPurpose,
                time,
                CancellationToken.None));
        Assert.True(File.Exists(armFile));

        HilArmManifest consumed = await HilArmManifest.ConsumeAsync(
            armFile,
            token,
            HilArmManifest.SafetyAuthenticationLossPurpose,
            time,
            CancellationToken.None);
        Assert.False(File.Exists(armFile));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToPulseOptions(consumed, armFile, token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyExpiryOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetySessionLossOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyEngineConnectionLossOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyProcessLossOptions(
                consumed,
                armFile,
                token));
    }

    [Fact]
    public void SafetyAuthenticationLossOptionsComeOnlyFromConsumedManifest()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "authentication-loss-arm.json");
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            purpose: HilArmManifest.SafetyAuthenticationLossPurpose);

        HilOptions safety =
            HilArmManifest.ToSafetyAuthenticationLossOptions(
                manifest,
                armFile,
                token);

        Assert.Equal(HilCommand.SafetyAuthenticationLoss, safety.Command);
        Assert.Equal(14_250_000, safety.FrequencyHz);
        Assert.Equal("ANT1", safety.TxAntenna);
        Assert.Equal(1, safety.RfPower);
        Assert.Equal(100, safety.KeyMilliseconds);
    }

    [Fact]
    public void SafetyGatewayProcessLossCommandsUseTheSameFixedOnAirBounds()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "gateway-process-loss-arm.json");
        HilOptions prepare = HilOptions.Parse(
        [
            "prepare-safety-gateway-loss",
            "--arm-file",
            armFile,
            "--frequency-hz",
            "14250000",
            "--on-air-confirm",
            HilOptions.RequiredOnAirConfirmation
        ]);
        HilOptions run = HilOptions.Parse(
        [
            "safety-gateway-loss",
            "--arm-file",
            armFile,
            "--token",
            "test-token"
        ]);

        Assert.Equal(
            HilCommand.PrepareSafetyGatewayProcessLoss,
            prepare.Command);
        Assert.Equal(HilCommand.SafetyGatewayProcessLoss, run.Command);
        Assert.Equal(14_250_000, prepare.FrequencyHz);
        Assert.Equal("ANT1", prepare.TxAntenna);
        Assert.Equal(1, prepare.RfPower);
        Assert.Equal(100, prepare.KeyMilliseconds);
    }

    [Fact]
    public async Task GatewayProcessLossManifestCannotLaunchAnotherOperation()
    {
        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(
            temporary.Path,
            "gateway-process-loss-arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 23, 0, 0, TimeSpan.Zero));
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            time,
            HilArmManifest.SafetyGatewayProcessLossPurpose);
        await HilArmManifest.WriteAsync(
            armFile,
            manifest,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilArmManifest.ConsumeAsync(
                armFile,
                token,
                HilArmManifest.SafetyAuthenticationLossPurpose,
                time,
                CancellationToken.None));
        Assert.True(File.Exists(armFile));

        HilArmManifest consumed = await HilArmManifest.ConsumeAsync(
            armFile,
            token,
            HilArmManifest.SafetyGatewayProcessLossPurpose,
            time,
            CancellationToken.None);
        Assert.False(File.Exists(armFile));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToPulseOptions(consumed, armFile, token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyExpiryOptions(consumed, armFile, token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetySessionLossOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyAuthenticationLossOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyEngineConnectionLossOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyProcessLossOptions(
                consumed,
                armFile,
                token));
    }

    [Fact]
    public void SafetyGatewayProcessLossOptionsComeOnlyFromConsumedManifest()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "gateway-process-loss-arm.json");
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            purpose: HilArmManifest.SafetyGatewayProcessLossPurpose);

        HilOptions safety =
            HilArmManifest.ToSafetyGatewayProcessLossOptions(
                manifest,
                armFile,
                token);

        Assert.Equal(HilCommand.SafetyGatewayProcessLoss, safety.Command);
        Assert.Equal(14_250_000, safety.FrequencyHz);
        Assert.Equal("ANT1", safety.TxAntenna);
        Assert.Equal(1, safety.RfPower);
        Assert.Equal(100, safety.KeyMilliseconds);
    }

    [Fact]
    public void SafetyEngineConnectionLossCommandsUseTheSameFixedOnAirBounds()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "engine-loss-arm.json");
        HilOptions prepare = HilOptions.Parse(
        [
            "prepare-safety-engine-loss",
            "--arm-file",
            armFile,
            "--frequency-hz",
            "14250000",
            "--on-air-confirm",
            HilOptions.RequiredOnAirConfirmation
        ]);
        HilOptions run = HilOptions.Parse(
        [
            "safety-engine-loss",
            "--arm-file",
            armFile,
            "--token",
            "test-token"
        ]);

        Assert.Equal(
            HilCommand.PrepareSafetyEngineConnectionLoss,
            prepare.Command);
        Assert.Equal(HilCommand.SafetyEngineConnectionLoss, run.Command);
        Assert.Equal(14_250_000, prepare.FrequencyHz);
        Assert.Equal("ANT1", prepare.TxAntenna);
        Assert.Equal(1, prepare.RfPower);
        Assert.Equal(100, prepare.KeyMilliseconds);
    }

    [Fact]
    public async Task EngineConnectionLossManifestCannotLaunchAnotherOperation()
    {
        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(
            temporary.Path,
            "engine-loss-arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            time,
            HilArmManifest.SafetyEngineConnectionLossPurpose);
        await HilArmManifest.WriteAsync(
            armFile,
            manifest,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilArmManifest.ConsumeAsync(
                armFile,
                token,
                HilArmManifest.SafetySessionLossPurpose,
                time,
                CancellationToken.None));
        Assert.True(File.Exists(armFile));

        HilArmManifest consumed = await HilArmManifest.ConsumeAsync(
            armFile,
            token,
            HilArmManifest.SafetyEngineConnectionLossPurpose,
            time,
            CancellationToken.None);
        Assert.False(File.Exists(armFile));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToPulseOptions(consumed, armFile, token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyExpiryOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetySessionLossOptions(
                consumed,
                armFile,
                token));
    }

    [Fact]
    public void SafetyEngineConnectionLossOptionsComeOnlyFromManifest()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "engine-loss-arm.json");
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            purpose: HilArmManifest.SafetyEngineConnectionLossPurpose);

        HilOptions safety =
            HilArmManifest.ToSafetyEngineConnectionLossOptions(
                manifest,
                armFile,
                token);

        Assert.Equal(HilCommand.SafetyEngineConnectionLoss, safety.Command);
        Assert.Equal(14_250_000, safety.FrequencyHz);
        Assert.Equal("ANT1", safety.TxAntenna);
        Assert.Equal(1, safety.RfPower);
        Assert.Equal(100, safety.KeyMilliseconds);
    }

    [Fact]
    public void SafetyProcessLossCommandsUseTheSameFixedOnAirBounds()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "process-loss-arm.json");
        HilOptions prepare = HilOptions.Parse(
        [
            "prepare-safety-process-loss",
            "--arm-file",
            armFile,
            "--frequency-hz",
            "14250000",
            "--on-air-confirm",
            HilOptions.RequiredOnAirConfirmation
        ]);
        HilOptions run = HilOptions.Parse(
        [
            "safety-process-loss",
            "--arm-file",
            armFile,
            "--token",
            "test-token"
        ]);

        Assert.Equal(HilCommand.PrepareSafetyProcessLoss, prepare.Command);
        Assert.Equal(HilCommand.SafetyProcessLoss, run.Command);
        Assert.Equal(14_250_000, prepare.FrequencyHz);
        Assert.Equal("ANT1", prepare.TxAntenna);
        Assert.Equal(1, prepare.RfPower);
        Assert.Equal(100, prepare.KeyMilliseconds);
    }

    [Fact]
    public async Task ProcessLossManifestCannotLaunchAnotherOperation()
    {
        using TemporaryDirectory temporary = new();
        string armFile = Path.Combine(
            temporary.Path,
            "process-loss-arm.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            time,
            HilArmManifest.SafetyProcessLossPurpose);
        await HilArmManifest.WriteAsync(
            armFile,
            manifest,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilArmManifest.ConsumeAsync(
                armFile,
                token,
                HilArmManifest.SafetyEngineConnectionLossPurpose,
                time,
                CancellationToken.None));
        Assert.True(File.Exists(armFile));

        HilArmManifest consumed = await HilArmManifest.ConsumeAsync(
            armFile,
            token,
            HilArmManifest.SafetyProcessLossPurpose,
            time,
            CancellationToken.None);
        Assert.False(File.Exists(armFile));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToPulseOptions(consumed, armFile, token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyExpiryOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetySessionLossOptions(
                consumed,
                armFile,
                token));
        Assert.Throws<InvalidOperationException>(() =>
            HilArmManifest.ToSafetyEngineConnectionLossOptions(
                consumed,
                armFile,
                token));
    }

    [Fact]
    public void SafetyProcessLossOptionsComeOnlyFromManifest()
    {
        string armFile = Path.Combine(
            Path.GetTempPath(),
            "process-loss-arm.json");
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) = HilArmManifest.Create(
            prepared,
            purpose: HilArmManifest.SafetyProcessLossPurpose);

        HilOptions safety = HilArmManifest.ToSafetyProcessLossOptions(
            manifest,
            armFile,
            token);

        Assert.Equal(HilCommand.SafetyProcessLoss, safety.Command);
        Assert.Equal(14_250_000, safety.FrequencyHz);
        Assert.Equal("ANT1", safety.TxAntenna);
        Assert.Equal(1, safety.RfPower);
        Assert.Equal(100, safety.KeyMilliseconds);
    }

    [Fact]
    public async Task EngineChildPlanStoresOnlyHashAndIsConsumedOnce()
    {
        using TemporaryDirectory temporary = new();
        string childFile = Path.Combine(temporary.Path, "child.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        using Process parent = Process.GetCurrentProcess();
        (HilEngineProcessChildPlan plan, string token) =
            HilEngineProcessChildPlan.Create(
                PrepareOptions(Path.Combine(temporary.Path, "arm.json")),
                parent,
                time);

        await HilEngineProcessChildPlan.WriteAsync(
            childFile,
            plan,
            CancellationToken.None);

        string stored = await File.ReadAllTextAsync(childFile);
        Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
        Assert.Contains(plan.TokenSha256, stored, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(childFile));
        }

        HilEngineProcessChildPlan consumed =
            await HilEngineProcessChildPlan.ConsumeAsync(
                childFile,
                token,
                time,
                CancellationToken.None);
        consumed.VerifyParentProcess();

        Assert.Equal(plan, consumed);
        Assert.False(File.Exists(childFile));
        Assert.Equal(
            HilEngineProcessChildPlan.Lifetime,
            consumed.ExpiresAt - consumed.CreatedAt);
    }

    [Fact]
    public async Task WrongChildTokenDoesNotConsumePlan()
    {
        using TemporaryDirectory temporary = new();
        string childFile = Path.Combine(temporary.Path, "child.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        using Process parent = Process.GetCurrentProcess();
        (HilEngineProcessChildPlan plan, string token) =
            HilEngineProcessChildPlan.Create(
                PrepareOptions(Path.Combine(temporary.Path, "arm.json")),
                parent,
                time);
        await HilEngineProcessChildPlan.WriteAsync(
            childFile,
            plan,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilEngineProcessChildPlan.ConsumeAsync(
                childFile,
                new string('0', token.Length),
                time,
                CancellationToken.None));

        Assert.True(File.Exists(childFile));
    }

    [Fact]
    public async Task ExpiredEngineChildPlanFailsClosed()
    {
        using TemporaryDirectory temporary = new();
        string childFile = Path.Combine(temporary.Path, "child.json");
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero));
        using Process parent = Process.GetCurrentProcess();
        (HilEngineProcessChildPlan plan, string token) =
            HilEngineProcessChildPlan.Create(
                PrepareOptions(Path.Combine(temporary.Path, "arm.json")),
                parent,
                time);
        await HilEngineProcessChildPlan.WriteAsync(
            childFile,
            plan,
            CancellationToken.None);
        time.Advance(HilEngineProcessChildPlan.Lifetime);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HilEngineProcessChildPlan.ConsumeAsync(
                childFile,
                token,
                time,
                CancellationToken.None));

        Assert.True(File.Exists(childFile));
    }

    [Fact]
    public void PulseOptionsComeOnlyFromConsumedManifest()
    {
        string armFile = Path.Combine(Path.GetTempPath(), "arm.json");
        HilOptions prepared = PrepareOptions(armFile);
        (HilArmManifest manifest, string token) =
            HilArmManifest.Create(prepared);

        HilOptions pulse = HilArmManifest.ToPulseOptions(
            manifest,
            armFile,
            token);

        Assert.Equal(HilCommand.Pulse, pulse.Command);
        Assert.Equal("ANT1", pulse.TxAntenna);
        Assert.Equal(HilOptions.FixedFirstPulseRfPower, pulse.RfPower);
        Assert.Equal(
            HilOptions.FixedFirstPulseMilliseconds,
            pulse.KeyMilliseconds);
        Assert.Equal(14_250_000, pulse.FrequencyHz);
        Assert.Equal(HilOptions.Psoc2Serial, pulse.ExpectedSerial);
    }

    private static HilOptions PrepareOptions(string armFile) =>
        HilOptions.Parse(
        [
            "prepare",
            "--arm-file",
            armFile,
            "--frequency-hz",
            "14250000",
            "--on-air-confirm",
            HilOptions.RequiredOnAirConfirmation
        ]);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration) =>
            m_now = m_now.Add(duration);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-tx-hil-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
