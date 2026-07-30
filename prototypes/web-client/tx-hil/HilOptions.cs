using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AetherSDR.TxHil;

internal enum HilCommand
{
    Inspect,
    RestoreIdleDefaults,
    VerifyExternalBlock,
    VerifyCwxConfiguration,
    VerifyPreflight,
    VerifySafetyFaults,
    VerifySafetyObserver,
    VerifySafetyExpiryPreflight,
    VerifySafetySessionLossPreflight,
    VerifySafetyEngineConnectionLossPreflight,
    VerifySafetyProcessLossPreflight,
    Prepare,
    Pulse,
    PrepareSafetyExpiry,
    SafetyExpiry,
    PrepareSafetySessionLoss,
    SafetySessionLoss,
    PrepareSafetyEngineConnectionLoss,
    SafetyEngineConnectionLoss,
    PrepareSafetyProcessLoss,
    SafetyProcessLoss
}

internal sealed record HilOptions(
    HilCommand Command,
    string Host,
    int Port,
    string RadioId,
    string ExpectedSerial,
    string TxAntenna,
    long FrequencyHz,
    string Mode,
    int RfPower,
    int KeyMilliseconds,
    string ArmFile,
    string Token,
    string SafetyConfirmation)
{
    public const string Psoc2RadioId = "FLEX:1121-1104-6700-2912";
    public const string Psoc2Serial = "1121-1104-6700-2912";
    public const string RequiredOnAirConfirmation =
        "KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF";
    public const long MinimumFirstPulseFrequencyHz = 14_225_000;
    public const long MaximumFirstPulseFrequencyHz = 14_350_000;
    public const int FixedFirstPulseMilliseconds = 100;
    public const int FixedFirstPulseRfPower = 1;
    public const int MaximumKeyMilliseconds = FixedFirstPulseMilliseconds;
    public const int MinimumKeyMilliseconds = FixedFirstPulseMilliseconds;
    public const int MaximumRfPower = FixedFirstPulseRfPower;
    public static readonly TimeSpan ArmLifetime = TimeSpan.FromMinutes(5);

    public static HilOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            throw new HilUsageException(Usage);
        }

        HilCommand command = args[0].ToLowerInvariant() switch
        {
            "inspect" => HilCommand.Inspect,
            "restore-idle-defaults" => HilCommand.RestoreIdleDefaults,
            "verify-external-block" => HilCommand.VerifyExternalBlock,
            "verify-cwx-config" => HilCommand.VerifyCwxConfiguration,
            "verify-preflight" => HilCommand.VerifyPreflight,
            "verify-safety-faults" => HilCommand.VerifySafetyFaults,
            "verify-safety-observer" => HilCommand.VerifySafetyObserver,
            "verify-safety-expiry-preflight" =>
                HilCommand.VerifySafetyExpiryPreflight,
            "verify-safety-session-loss-preflight" =>
                HilCommand.VerifySafetySessionLossPreflight,
            "verify-safety-engine-loss-preflight" =>
                HilCommand.VerifySafetyEngineConnectionLossPreflight,
            "verify-safety-process-loss-preflight" =>
                HilCommand.VerifySafetyProcessLossPreflight,
            "prepare" => HilCommand.Prepare,
            "pulse" => HilCommand.Pulse,
            "prepare-safety-expiry" => HilCommand.PrepareSafetyExpiry,
            "safety-expiry" => HilCommand.SafetyExpiry,
            "prepare-safety-session-loss" =>
                HilCommand.PrepareSafetySessionLoss,
            "safety-session-loss" => HilCommand.SafetySessionLoss,
            "prepare-safety-engine-loss" =>
                HilCommand.PrepareSafetyEngineConnectionLoss,
            "safety-engine-loss" => HilCommand.SafetyEngineConnectionLoss,
            "prepare-safety-process-loss" =>
                HilCommand.PrepareSafetyProcessLoss,
            "safety-process-loss" => HilCommand.SafetyProcessLoss,
            _ => throw new HilUsageException(
                $"Unknown command '{args[0]}'.\n\n{Usage}")
        };

        Dictionary<string, string> values = ParsePairs(args[1..]);
        string host = Read(values, "host", "10.2.0.12");
        int port = ReadInt(values, "port", 4992, 1, 65_535);
        string radioId = Read(values, "radio-id", Psoc2RadioId).ToUpperInvariant();
        string expectedSerial = Read(values, "expected-serial", Psoc2Serial);
        string txAntenna = Read(values, "tx-antenna", "ANT1").ToUpperInvariant();
        long frequencyHz = ReadLong(
            values,
            "frequency-hz",
            MinimumFirstPulseFrequencyHz,
            100_000,
            60_000_000);
        string mode = Read(values, "mode", "USB").ToUpperInvariant();
        int rfPower = ReadInt(values, "rf-power", 1, 1, MaximumRfPower);
        int keyMilliseconds = ReadInt(
            values,
            "key-ms",
            FixedFirstPulseMilliseconds,
            MinimumKeyMilliseconds,
            MaximumKeyMilliseconds);
        string armFile = Read(values, "arm-file", string.Empty);
        string token = Read(values, "token", string.Empty);
        string safetyConfirmation =
            Read(values, "on-air-confirm", string.Empty);

        ValidateIdentifier(host, 128, "host");
        ValidateIdentifier(radioId, 128, "radio-id");
        ValidateIdentifier(expectedSerial, 128, "expected-serial");
        ValidateIdentifier(txAntenna, 32, "tx-antenna");
        ValidateIdentifier(mode, 16, "mode");

        if (!string.Equals(radioId, Psoc2RadioId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expectedSerial, Psoc2Serial, StringComparison.Ordinal))
        {
            throw new HilUsageException(
                "This first HIL harness is hard-bound to PSOC2 serial " +
                $"{Psoc2Serial}; another radio is not permitted.");
        }

        if (command is
            HilCommand.VerifyPreflight or
            HilCommand.VerifySafetyExpiryPreflight or
            HilCommand.VerifySafetySessionLossPreflight or
            HilCommand.VerifySafetyEngineConnectionLossPreflight or
            HilCommand.VerifySafetyProcessLossPreflight or
            HilCommand.Prepare or
            HilCommand.Pulse or
            HilCommand.PrepareSafetyExpiry or
            HilCommand.SafetyExpiry or
            HilCommand.PrepareSafetySessionLoss or
            HilCommand.SafetySessionLoss or
            HilCommand.PrepareSafetyEngineConnectionLoss or
            HilCommand.SafetyEngineConnectionLoss or
            HilCommand.PrepareSafetyProcessLoss or
            HilCommand.SafetyProcessLoss)
        {
            if (!string.Equals(mode, "USB", StringComparison.Ordinal) ||
                txAntenna != "ANT1" ||
                rfPower != FixedFirstPulseRfPower ||
                keyMilliseconds != FixedFirstPulseMilliseconds)
            {
                throw new HilUsageException(
                    "The first on-air PSOC2 HIL pulse is fixed at USB, ANT1, 1 W, and 100 ms.");
            }
            if (command is
                HilCommand.Prepare or
                HilCommand.Pulse or
                HilCommand.PrepareSafetyExpiry or
                HilCommand.SafetyExpiry or
                HilCommand.PrepareSafetySessionLoss or
                HilCommand.SafetySessionLoss or
                HilCommand.PrepareSafetyEngineConnectionLoss or
                HilCommand.SafetyEngineConnectionLoss or
                HilCommand.PrepareSafetyProcessLoss or
                HilCommand.SafetyProcessLoss)
            {
                if (string.IsNullOrWhiteSpace(armFile))
                {
                    throw new HilUsageException(
                        "--arm-file is required for prepare and pulse.");
                }
                armFile = Path.GetFullPath(armFile);
            }
        }

        if (command is
            HilCommand.VerifyPreflight or
            HilCommand.VerifySafetyExpiryPreflight or
            HilCommand.VerifySafetySessionLossPreflight or
            HilCommand.VerifySafetyEngineConnectionLossPreflight or
            HilCommand.VerifySafetyProcessLossPreflight or
            HilCommand.Prepare or
            HilCommand.PrepareSafetyExpiry or
            HilCommand.PrepareSafetySessionLoss or
            HilCommand.PrepareSafetyEngineConnectionLoss or
            HilCommand.PrepareSafetyProcessLoss)
        {
            if (!values.ContainsKey("frequency-hz") ||
                frequencyHz < MinimumFirstPulseFrequencyHz ||
                frequencyHz > MaximumFirstPulseFrequencyHz)
            {
                throw new HilUsageException(
                    $"--frequency-hz is required and must be between {MinimumFirstPulseFrequencyHz} and {MaximumFirstPulseFrequencyHz} for the first on-air pulse.");
            }
            if (!string.Equals(
                    safetyConfirmation,
                    RequiredOnAirConfirmation,
                    StringComparison.Ordinal))
            {
                throw new HilUsageException(
                    "The exact on-air safety confirmation is required:\n" +
                    RequiredOnAirConfirmation);
            }
        }

        if ((command is
                HilCommand.Pulse or
                HilCommand.SafetyExpiry or
                HilCommand.SafetySessionLoss or
                HilCommand.SafetyEngineConnectionLoss or
                HilCommand.SafetyProcessLoss) &&
            string.IsNullOrWhiteSpace(token))
        {
            throw new HilUsageException("--token is required for the armed HIL operation.");
        }

        return new HilOptions(
            command,
            host,
            port,
            radioId,
            expectedSerial,
            txAntenna,
            frequencyHz,
            mode,
            rfPower,
            keyMilliseconds,
            armFile,
            token,
            safetyConfirmation);
    }

    public static string Usage =>
        "AetherSDR PSOC2 TX hardware-in-the-loop harness\n\n" +
        "Commands:\n" +
        "  inspect                  Read-only identity/interlock/client roster\n" +
        "  restore-idle-defaults    Restore PSOC2 idle TX route to 100 W/PC/DAX/VOX-off\n" +
        "  verify-external-block    Prove external Local PTT blocks the gate\n" +
        "  verify-cwx-config        Round-trip CWX settings without sending text\n" +
        "  verify-preflight         Stage and clean up the radio without keying\n" +
        "  verify-safety-faults     Run the simulated independent-watchdog matrix\n" +
        "  verify-safety-observer   Verify the live non-GUI unkey-only observer\n" +
        "  verify-safety-expiry-preflight\n" +
        "                           Stage the heartbeat-expiry test without RF\n" +
        "  verify-safety-session-loss-preflight\n" +
        "                           Stage the browser-session-loss test without RF\n" +
        "  verify-safety-engine-loss-preflight\n" +
        "                           Stage engine TX command-channel loss without RF\n" +
        "  verify-safety-process-loss-preflight\n" +
        "                           Kill a child engine process while idle, without RF\n" +
        "  prepare                  Create a five-minute normal-pulse manifest\n" +
        "  pulse                    Run the operator-unkey pulse and CW ID\n" +
        "  prepare-safety-expiry    Create a five-minute heartbeat-expiry manifest\n" +
        "  safety-expiry            Let the observer unkey after heartbeat expiry\n" +
        "  prepare-safety-session-loss\n" +
        "                           Create a five-minute session-loss manifest\n" +
        "  safety-session-loss      Release the browser session and observer-unkey\n" +
        "  prepare-safety-engine-loss\n" +
        "                           Create an engine command-channel-loss manifest\n" +
        "  safety-engine-loss       Disable engine TX commands and observer-unkey\n" +
        "  prepare-safety-process-loss\n" +
        "                           Create a true engine-process-loss manifest\n" +
        "  safety-process-loss      Kill the engine process and reconcile TX\n\n" +
        "Safety defaults:\n" +
        $"  radio-id={Psoc2RadioId}\n" +
        $"  expected-serial={Psoc2Serial}\n" +
        "  host=10.2.0.12 port=4992 tx-antenna=ANT1\n" +
        "  mode=USB rf-power=1 key-ms=100\n" +
        $"  frequency-hz must be explicitly supplied between {MinimumFirstPulseFrequencyHz} and {MaximumFirstPulseFrequencyHz}\n\n" +
        "Prepare example after listening and confirming the frequency is clear:\n" +
        "  AetherSDR.TxHil prepare --arm-file /run/user/$UID/aethersdr-tx-hil.json \\\n" +
        "    --frequency-hz <clear-frequency-hz> \\\n" +
        $"    --on-air-confirm \"{RequiredOnAirConfirmation}\"\n\n" +
        "Pulse example (SmartSDR and every other external GUI must be disconnected):\n" +
        "  AetherSDR.TxHil pulse --arm-file /run/user/$UID/aethersdr-tx-hil.json \\\n" +
        "    --token <one-time-token>\n";

    private static Dictionary<string, string> ParsePairs(string[] args)
    {
        Dictionary<string, string> values =
            new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) ||
                argument.Length <= 2)
            {
                throw new HilUsageException(
                    $"Expected --name value, received '{argument}'.");
            }

            string name;
            string value;
            int equals = argument.IndexOf('=');
            if (equals > 2)
            {
                name = argument[2..equals];
                value = argument[(equals + 1)..];
            }
            else
            {
                name = argument[2..];
                if (++index >= args.Length)
                {
                    throw new HilUsageException(
                        $"Option --{name} requires a value.");
                }
                value = args[index];
            }

            if (!values.TryAdd(name, value))
            {
                throw new HilUsageException(
                    $"Option --{name} was supplied more than once.");
            }
        }
        return values;
    }

    private static string Read(
        IReadOnlyDictionary<string, string> values,
        string name,
        string fallback) =>
        values.TryGetValue(name, out string? value)
            ? value.Trim()
            : fallback;

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        string text = Read(values, name, fallback.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new HilUsageException(
                $"--{name} must be between {minimum} and {maximum}.");
        }
        return parsed;
    }

    private static long ReadLong(
        IReadOnlyDictionary<string, string> values,
        string name,
        long fallback,
        long minimum,
        long maximum)
    {
        string text = Read(values, name, fallback.ToString(CultureInfo.InvariantCulture));
        if (!long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new HilUsageException(
                $"--{name} must be between {minimum} and {maximum}.");
        }
        return parsed;
    }

    private static void ValidateIdentifier(
        string value,
        int maximumLength,
        string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new HilUsageException($"--{name} is invalid.");
        }
    }
}

internal sealed record HilArmManifest(
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string TokenSha256,
    string Purpose,
    string Host,
    int Port,
    string RadioId,
    string ExpectedSerial,
    string TxAntenna,
    long FrequencyHz,
    string Mode,
    int RfPower,
    int KeyMilliseconds)
{
    public const int CurrentVersion = 2;
    public const string NormalPulsePurpose = "operator-unkey-pulse";
    public const string SafetyExpiryPurpose = "independent-heartbeat-expiry";
    public const string SafetySessionLossPurpose =
        "independent-browser-session-loss";
    public const string SafetyEngineConnectionLossPurpose =
        "independent-engine-connection-loss";
    public const string SafetyProcessLossPurpose =
        "independent-engine-process-loss";

    public static (HilArmManifest Manifest, string Token) Create(
        HilOptions options,
        TimeProvider? timeProvider = null,
        string purpose = NormalPulsePurpose)
    {
        ArgumentNullException.ThrowIfNull(options);
        DateTimeOffset now =
            (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (purpose is not (
                NormalPulsePurpose or
                SafetyExpiryPurpose or
                SafetySessionLossPurpose or
                SafetyEngineConnectionLossPurpose or
                SafetyProcessLossPurpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }
        string token = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(24));
        HilArmManifest manifest = new(
            CurrentVersion,
            now,
            now + HilOptions.ArmLifetime,
            HashToken(token),
            purpose,
            options.Host,
            options.Port,
            options.RadioId,
            options.ExpectedSerial,
            options.TxAntenna,
            options.FrequencyHz,
            options.Mode,
            options.RfPower,
            options.KeyMilliseconds);
        return (manifest, token);
    }

    public static async Task WriteAsync(
        string path,
        HilArmManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(manifest);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new InvalidOperationException(
                "The arm-file directory must already exist.");
        }
        if (File.Exists(fullPath))
        {
            throw new InvalidOperationException(
                "The arm file already exists; remove it explicitly before preparing another pulse.");
        }

        await using FileStream stream = new(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough | FileOptions.Asynchronous);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await JsonSerializer.SerializeAsync(
            stream,
            manifest,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static Task<HilArmManifest> ConsumeAsync(
        string path,
        string token,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken) =>
        ConsumeAsync(
            path,
            token,
            NormalPulsePurpose,
            timeProvider,
            cancellationToken);

    public static async Task<HilArmManifest> ConsumeAsync(
        string path,
        string token,
        string expectedPurpose,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() &&
            File.GetUnixFileMode(fullPath) !=
                (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            throw new InvalidOperationException(
                "The arm manifest must be owned by the operator and have exact mode 0600.");
        }

        HilArmManifest manifest;
        await using (FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            4096,
            FileOptions.Asynchronous))
        {
            manifest = await JsonSerializer.DeserializeAsync<HilArmManifest>(
                    stream,
                    cancellationToken: cancellationToken) ??
                throw new InvalidOperationException("The arm manifest is empty.");
        }

        DateTimeOffset now =
            (timeProvider ?? TimeProvider.System).GetUtcNow();
        Validate(manifest, token, expectedPurpose, now);
        File.Delete(fullPath);
        return manifest;
    }

    public static HilOptions ToPulseOptions(
        HilArmManifest manifest,
        string armFile,
        string token) =>
        ToOptions(manifest, HilCommand.Pulse, armFile, token);

    public static HilOptions ToSafetyExpiryOptions(
        HilArmManifest manifest,
        string armFile,
        string token) =>
        ToOptions(manifest, HilCommand.SafetyExpiry, armFile, token);

    public static HilOptions ToSafetySessionLossOptions(
        HilArmManifest manifest,
        string armFile,
        string token) =>
        ToOptions(manifest, HilCommand.SafetySessionLoss, armFile, token);

    public static HilOptions ToSafetyEngineConnectionLossOptions(
        HilArmManifest manifest,
        string armFile,
        string token) =>
        ToOptions(
            manifest,
            HilCommand.SafetyEngineConnectionLoss,
            armFile,
            token);

    public static HilOptions ToSafetyProcessLossOptions(
        HilArmManifest manifest,
        string armFile,
        string token) =>
        ToOptions(
            manifest,
            HilCommand.SafetyProcessLoss,
            armFile,
            token);

    private static HilOptions ToOptions(
        HilArmManifest manifest,
        HilCommand command,
        string armFile,
        string token)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string expectedPurpose = command switch
        {
            HilCommand.Pulse => NormalPulsePurpose,
            HilCommand.SafetyExpiry => SafetyExpiryPurpose,
            HilCommand.SafetySessionLoss => SafetySessionLossPurpose,
            HilCommand.SafetyEngineConnectionLoss =>
                SafetyEngineConnectionLossPurpose,
            HilCommand.SafetyProcessLoss => SafetyProcessLossPurpose,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        if (!string.Equals(
                manifest.Purpose,
                expectedPurpose,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The arm manifest purpose does not match the requested HIL operation.");
        }
        return new HilOptions(
            command,
            manifest.Host,
            manifest.Port,
            manifest.RadioId,
            manifest.ExpectedSerial,
            manifest.TxAntenna,
            manifest.FrequencyHz,
            manifest.Mode,
            manifest.RfPower,
            manifest.KeyMilliseconds,
            armFile,
            token,
            HilOptions.RequiredOnAirConfirmation);
    }

    private static void Validate(
        HilArmManifest manifest,
        string token,
        string expectedPurpose,
        DateTimeOffset now)
    {
        bool tokenMatches = false;
        try
        {
            tokenMatches =
                manifest.TokenSha256.Length == 64 &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(manifest.TokenSha256),
                    Convert.FromHexString(HashToken(token)));
        }
        catch (FormatException)
        {
            tokenMatches = false;
        }

        if (expectedPurpose is not (
                NormalPulsePurpose or
                SafetyExpiryPurpose or
                SafetySessionLossPurpose or
                SafetyEngineConnectionLossPurpose or
                SafetyProcessLossPurpose) ||
            manifest.Version != CurrentVersion ||
            !string.Equals(
                manifest.Purpose,
                expectedPurpose,
                StringComparison.Ordinal) ||
            manifest.CreatedAt > now + TimeSpan.FromSeconds(5) ||
            manifest.ExpiresAt <= now ||
            manifest.ExpiresAt - manifest.CreatedAt > HilOptions.ArmLifetime ||
            !string.Equals(
                manifest.RadioId,
                HilOptions.Psoc2RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                manifest.ExpectedSerial,
                HilOptions.Psoc2Serial,
                StringComparison.Ordinal) ||
            manifest.FrequencyHz < HilOptions.MinimumFirstPulseFrequencyHz ||
            manifest.FrequencyHz > HilOptions.MaximumFirstPulseFrequencyHz ||
            !string.Equals(manifest.Mode, "USB", StringComparison.Ordinal) ||
            manifest.TxAntenna != "ANT1" ||
            manifest.RfPower != HilOptions.FixedFirstPulseRfPower ||
            manifest.KeyMilliseconds != HilOptions.FixedFirstPulseMilliseconds ||
            !tokenMatches)
        {
            throw new InvalidOperationException(
                "The arm manifest is expired, mismatched, or the one-time token is invalid.");
        }
    }

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
}

internal sealed class HilUsageException(string message) : Exception(message);
