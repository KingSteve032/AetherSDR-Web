using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AetherSDR.TxHil;

internal sealed record HilEngineProcessChildPlan(
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string TokenSha256,
    int ParentProcessId,
    long ParentStartTimeUtcTicks,
    string EngineInstanceId,
    string SessionId,
    string BrowserClientId,
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
    public const int CurrentVersion = 1;
    public const string Purpose = "engine-process-child";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public static (HilEngineProcessChildPlan Plan, string Token) Create(
        HilOptions options,
        Process parent,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parent);
        DateTimeOffset now =
            (timeProvider ?? TimeProvider.System).GetUtcNow();
        string token = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(24));
        HilEngineProcessChildPlan plan = new(
            CurrentVersion,
            now,
            now + Lifetime,
            HashToken(token),
            parent.Id,
            parent.StartTime.ToUniversalTime().Ticks,
            $"tx-hil-engine-process-{Guid.NewGuid():N}",
            $"tx-hil-process-session-{Guid.NewGuid():N}",
            $"tx-hil-process-browser-{Guid.NewGuid():N}",
            options.Host,
            options.Port,
            options.RadioId,
            options.ExpectedSerial,
            options.TxAntenna,
            options.FrequencyHz,
            options.Mode,
            options.RfPower,
            options.KeyMilliseconds);
        return (plan, token);
    }

    public static async Task WriteAsync(
        string path,
        HilEngineProcessChildPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new InvalidOperationException(
                "The child-plan directory must already exist.");
        }
        if (File.Exists(fullPath))
        {
            throw new InvalidOperationException(
                "The child plan already exists; remove it explicitly before continuing.");
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
            plan,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<HilEngineProcessChildPlan> ConsumeAsync(
        string path,
        string token,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException("The child plan does not exist.");
        }
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The child plan must be a regular file, not a link.");
        }
        if (!OperatingSystem.IsWindows() &&
            File.GetUnixFileMode(fullPath) !=
                (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            throw new InvalidOperationException(
                "The child plan must have exact mode 0600.");
        }

        HilEngineProcessChildPlan plan;
        await using (FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            4096,
            FileOptions.Asynchronous))
        {
            plan = await JsonSerializer.DeserializeAsync<HilEngineProcessChildPlan>(
                    stream,
                    cancellationToken: cancellationToken) ??
                throw new InvalidOperationException("The child plan is empty.");
        }

        DateTimeOffset now =
            (timeProvider ?? TimeProvider.System).GetUtcNow();
        Validate(plan, token, now);
        File.Delete(fullPath);
        return plan;
    }

    public HilOptions ToHilOptions() =>
        new(
            HilCommand.VerifySafetyProcessLossPreflight,
            Host,
            Port,
            RadioId,
            ExpectedSerial,
            TxAntenna,
            FrequencyHz,
            Mode,
            RfPower,
            KeyMilliseconds,
            string.Empty,
            string.Empty,
            HilOptions.RequiredOnAirConfirmation);

    public void VerifyParentProcess()
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(ParentProcessId);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "The authorizing parent process no longer exists.",
                exception);
        }
        using (parent)
        {
            long observedStartTicks =
                parent.StartTime.ToUniversalTime().Ticks;
            long startDifference = Math.Abs(
                observedStartTicks - ParentStartTimeUtcTicks);
            // Linux reconstructs Process.StartTime from boot time and may differ
            // slightly between processes. PID plus a one-second bound remains
            // unambiguous within this plan's 30-second lifetime.
            if (parent.HasExited ||
                startDifference > TimeSpan.FromSeconds(1).Ticks)
            {
                throw new InvalidOperationException(
                    "The authorizing parent process identity no longer matches.");
            }
        }
    }

    private static void Validate(
        HilEngineProcessChildPlan plan,
        string token,
        DateTimeOffset now)
    {
        bool tokenMatches = false;
        try
        {
            tokenMatches =
                plan.TokenSha256.Length == 64 &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(plan.TokenSha256),
                    Convert.FromHexString(HashToken(token)));
        }
        catch (FormatException)
        {
            tokenMatches = false;
        }

        if (plan.Version != CurrentVersion ||
            plan.CreatedAt > now + TimeSpan.FromSeconds(5) ||
            plan.ExpiresAt <= now ||
            plan.ExpiresAt - plan.CreatedAt > Lifetime ||
            plan.ParentProcessId <= 0 ||
            plan.ParentStartTimeUtcTicks <= 0 ||
            !ValidIdentifier(plan.EngineInstanceId, 128) ||
            !ValidIdentifier(plan.SessionId, 128) ||
            !ValidIdentifier(plan.BrowserClientId, 128) ||
            !string.Equals(
                plan.RadioId,
                HilOptions.Psoc2RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                plan.ExpectedSerial,
                HilOptions.Psoc2Serial,
                StringComparison.Ordinal) ||
            plan.FrequencyHz < HilOptions.MinimumFirstPulseFrequencyHz ||
            plan.FrequencyHz > HilOptions.MaximumFirstPulseFrequencyHz ||
            !string.Equals(plan.Mode, "USB", StringComparison.Ordinal) ||
            plan.TxAntenna != "ANT1" ||
            plan.RfPower != HilOptions.FixedFirstPulseRfPower ||
            plan.KeyMilliseconds != HilOptions.FixedFirstPulseMilliseconds ||
            !tokenMatches)
        {
            throw new InvalidOperationException(
                "The child plan is expired, mismatched, or its one-time token is invalid.");
        }
    }

    private static bool ValidIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character));

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
}

internal sealed record HilEngineProcessChildOptions(
    string PlanFile,
    string Token)
{
    public static HilEngineProcessChildOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Dictionary<string, string> values =
            new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) ||
                argument.Length <= 2 || ++index >= args.Length)
            {
                throw new HilUsageException(
                    "The internal engine child requires --child-plan and --child-token.");
            }
            if (!values.TryAdd(argument[2..], args[index]))
            {
                throw new HilUsageException(
                    $"Internal option {argument} was supplied more than once.");
            }
        }
        if (!values.TryGetValue("child-plan", out string? planFile) ||
            !values.TryGetValue("child-token", out string? token) ||
            values.Count != 2 ||
            string.IsNullOrWhiteSpace(planFile) ||
            string.IsNullOrWhiteSpace(token))
        {
            throw new HilUsageException(
                "The internal engine child requires exactly --child-plan and --child-token.");
        }
        return new HilEngineProcessChildOptions(
            Path.GetFullPath(planFile),
            token.Trim());
    }
}
