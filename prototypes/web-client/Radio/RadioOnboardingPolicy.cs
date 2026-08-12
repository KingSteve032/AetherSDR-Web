using System.Text.Json;

namespace AetherSDR.Web.Radio;

public static class RadioTransmitPolicyStates
{
    public const string ReceiveOnly = "receive-only";
    public const string TxEligible = "tx-eligible";
    public const string TemporarilyDisabled = "temporarily-disabled";
    public const string PrerequisitesFailed = "prerequisites-failed";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is
            ReceiveOnly or
            TxEligible or
            TemporarilyDisabled or
            PrerequisitesFailed;
    }
}

public sealed record RadioOnboardingPolicySnapshot(
    string RadioId,
    string? Label,
    string TransmitPolicyState,
    bool Onboarded,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

public sealed record UpdateRadioOnboardingRequest(string Label);

public sealed class RadioOnboardingPolicyStore
{
    private const int FileVersion = 1;
    private const long MaximumFileBytes = 1024 * 1024;
    private const int MaximumPolicies = 1024;
    private readonly object m_gate = new();
    private readonly string m_policyPath;
    private readonly TimeProvider m_timeProvider;
    private readonly ILogger<RadioOnboardingPolicyStore> m_logger;
    private readonly Dictionary<string, RadioOnboardingPolicySnapshot> m_policies =
        new(StringComparer.OrdinalIgnoreCase);

    public RadioOnboardingPolicyStore(
        string policyPath,
        ILogger<RadioOnboardingPolicyStore> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyPath);
        m_policyPath = Path.GetFullPath(policyPath);
        m_logger = logger;
        m_timeProvider = timeProvider ?? TimeProvider.System;
        Load();
    }

    public RadioOnboardingPolicySnapshot GetPolicy(string radioId)
    {
        string normalizedRadioId = ValidateIdentifier(
            radioId,
            nameof(radioId),
            128);
        lock (m_gate)
        {
            return m_policies.TryGetValue(
                normalizedRadioId,
                out RadioOnboardingPolicySnapshot? policy)
                ? policy
                : DefaultPolicy(normalizedRadioId);
        }
    }

    public RadioOnboardingPolicySnapshot UpdateLabel(
        string radioId,
        string? label,
        string updatedBy)
    {
        string normalizedRadioId = ValidateIdentifier(
            radioId,
            nameof(radioId),
            128);
        string normalizedLabel = ValidateIdentifier(
            label,
            nameof(label),
            64);
        string normalizedUpdatedBy = ValidateIdentifier(
            updatedBy,
            nameof(updatedBy),
            256);

        lock (m_gate)
        {
            bool hadPreviousPolicy = m_policies.TryGetValue(
                normalizedRadioId,
                out RadioOnboardingPolicySnapshot? previousPolicy);
            RadioOnboardingPolicySnapshot current =
                previousPolicy ?? DefaultPolicy(normalizedRadioId);
            RadioOnboardingPolicySnapshot replacement = current with
            {
                Label = normalizedLabel,
                Onboarded = true,
                UpdatedAt = m_timeProvider.GetUtcNow(),
                UpdatedBy = normalizedUpdatedBy
            };
            m_policies[normalizedRadioId] = replacement;
            try
            {
                Persist();
            }
            catch
            {
                if (hadPreviousPolicy)
                {
                    m_policies[normalizedRadioId] = previousPolicy!;
                }
                else
                {
                    m_policies.Remove(normalizedRadioId);
                }
                throw;
            }

            m_logger.LogInformation(
                "Radio onboarding label updated for {RadioId}",
                normalizedRadioId);
            return replacement;
        }
    }

    private void Load()
    {
        if (!File.Exists(m_policyPath))
        {
            return;
        }

        FileInfo info = new(m_policyPath);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length is <= 0 or > MaximumFileBytes)
        {
            throw new InvalidDataException(
                "The radio onboarding policy file is not a bounded regular file.");
        }

        using FileStream stream = new(
            m_policyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        RadioOnboardingPolicyFile? file =
            JsonSerializer.Deserialize<RadioOnboardingPolicyFile>(
                stream,
                JsonOptions());
        if (file is null ||
            file.Version != FileVersion ||
            file.Policies.Count > MaximumPolicies)
        {
            throw new InvalidDataException(
                "The radio onboarding policy file has an unsupported format.");
        }

        foreach (RadioOnboardingPolicySnapshot policy in file.Policies)
        {
            string radioId = ValidateLoadedIdentifier(
                policy.RadioId,
                nameof(policy.RadioId),
                128);
            string? label = string.IsNullOrWhiteSpace(policy.Label)
                ? null
                : ValidateLoadedIdentifier(
                    policy.Label,
                    nameof(policy.Label),
                    64);
            if (!RadioTransmitPolicyStates.TryNormalize(
                    policy.TransmitPolicyState,
                    out string normalizedState))
            {
                throw new InvalidDataException(
                    $"Radio '{radioId}' has an invalid transmit policy state.");
            }

            string? updatedBy = string.IsNullOrWhiteSpace(policy.UpdatedBy)
                ? null
                : ValidateLoadedIdentifier(
                    policy.UpdatedBy,
                    nameof(policy.UpdatedBy),
                    256);
            RadioOnboardingPolicySnapshot normalized = policy with
            {
                RadioId = radioId,
                Label = label,
                TransmitPolicyState = normalizedState,
                UpdatedBy = updatedBy
            };
            if (!m_policies.TryAdd(radioId, normalized))
            {
                throw new InvalidDataException(
                    "The radio onboarding policy file contains duplicate radios.");
            }
        }
    }

    private void Persist()
    {
        string? directory = Path.GetDirectoryName(m_policyPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The radio onboarding policy path must include a directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{m_policyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            RadioOnboardingPolicyFile file = new(
                FileVersion,
                m_policies.Values
                    .OrderBy(
                        policy => policy.RadioId,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        temporaryPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                JsonSerializer.Serialize(stream, file, JsonOptions());
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, m_policyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static RadioOnboardingPolicySnapshot DefaultPolicy(string radioId) =>
        new(
            radioId,
            null,
            RadioTransmitPolicyStates.ReceiveOnly,
            Onboarded: false,
            UpdatedAt: null,
            UpdatedBy: null);

    private static string ValidateIdentifier(
        string? value,
        string parameterName,
        int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 ||
            normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A valid {parameterName} is required.",
                parameterName);
        }

        return normalized;
    }

    private static string ValidateLoadedIdentifier(
        string? value,
        string fieldName,
        int maximumLength)
    {
        try
        {
            return ValidateIdentifier(value, fieldName, maximumLength);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The radio onboarding policy file contains an invalid {fieldName}.",
                exception);
        }
    }

    private static JsonSerializerOptions JsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private sealed record RadioOnboardingPolicyFile(
        int Version,
        IReadOnlyList<RadioOnboardingPolicySnapshot> Policies);
}
