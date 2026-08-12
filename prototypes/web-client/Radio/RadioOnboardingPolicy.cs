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

public sealed record RadioOnboardingIdentity(
    string RadioId,
    string Source,
    string StationId,
    string SourceRadioId);

public sealed record RadioTransmitPreflightSnapshot(
    bool ValidationOnly,
    string TargetRadioId,
    bool Ready,
    string Reason,
    IReadOnlyList<string> MissingPrerequisites,
    DateTimeOffset EvaluatedAt);

public sealed record RadioOnboardingPolicySnapshot(
    string RadioId,
    string Source,
    string StationId,
    string SourceRadioId,
    string? Label,
    string TransmitPolicyState,
    bool Onboarded,
    RadioTransmitPreflightSnapshot? TransmitPreflight,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

public sealed record UpdateRadioOnboardingRequest(string Label);

public sealed record UpdateRadioTransmitPolicyRequest(string State);

public sealed class RadioOnboardingPolicyStore
{
    private const int FileVersion = 2;
    private const long MaximumFileBytes = 1024 * 1024;
    private const int MaximumPolicies = 1024;
    private const int MaximumMissingPrerequisites = 64;
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

    public RadioOnboardingPolicySnapshot GetPolicy(
        RadioOnboardingIdentity identity)
    {
        RadioOnboardingIdentity normalized = NormalizeIdentity(identity);
        lock (m_gate)
        {
            return m_policies.TryGetValue(
                    normalized.RadioId,
                    out RadioOnboardingPolicySnapshot? policy) &&
                IdentityMatches(policy, normalized)
                ? policy
                : DefaultPolicy(normalized);
        }
    }

    public RadioOnboardingPolicySnapshot UpdateLabel(
        RadioOnboardingIdentity identity,
        string? label,
        string updatedBy)
    {
        RadioOnboardingIdentity normalized = NormalizeIdentity(identity);
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
                normalized.RadioId,
                out RadioOnboardingPolicySnapshot? previousPolicy);
            EnsureCapacity(hadPreviousPolicy);
            RadioOnboardingPolicySnapshot current =
                hadPreviousPolicy &&
                IdentityMatches(previousPolicy!, normalized)
                    ? previousPolicy!
                    : DefaultPolicy(normalized);
            RadioOnboardingPolicySnapshot replacement = current with
            {
                Label = normalizedLabel,
                Onboarded = true,
                UpdatedAt = m_timeProvider.GetUtcNow(),
                UpdatedBy = normalizedUpdatedBy
            };
            ReplaceAndPersist(
                normalized.RadioId,
                replacement,
                hadPreviousPolicy,
                previousPolicy);
            m_logger.LogInformation(
                "Radio onboarding label updated for {RadioId}",
                normalized.RadioId);
            return replacement;
        }
    }

    public RadioOnboardingPolicySnapshot UpdateTransmitPolicy(
        RadioOnboardingIdentity identity,
        string? state,
        string updatedBy,
        RadioTransmitPreflightSnapshot? preflight = null)
    {
        RadioOnboardingIdentity normalized = NormalizeIdentity(identity);
        string normalizedUpdatedBy = ValidateIdentifier(
            updatedBy,
            nameof(updatedBy),
            256);
        if (!RadioTransmitPolicyStates.TryNormalize(
                state,
                out string normalizedState))
        {
            throw new ArgumentException(
                "A valid transmit policy state is required.",
                nameof(state));
        }

        lock (m_gate)
        {
            if (!m_policies.TryGetValue(
                    normalized.RadioId,
                    out RadioOnboardingPolicySnapshot? current) ||
                !IdentityMatches(current, normalized) ||
                !current.Onboarded ||
                string.IsNullOrWhiteSpace(current.Label))
            {
                throw new InvalidOperationException(
                    "The exact radio identity must be labeled and onboarded " +
                    "before its transmit policy can change.");
            }

            RadioTransmitPreflightSnapshot? validatedPreflight =
                ValidateTransition(
                    normalized,
                    normalizedState,
                    preflight);
            RadioOnboardingPolicySnapshot replacement = current with
            {
                TransmitPolicyState = normalizedState,
                TransmitPreflight = validatedPreflight,
                UpdatedAt = m_timeProvider.GetUtcNow(),
                UpdatedBy = normalizedUpdatedBy
            };
            ReplaceAndPersist(
                normalized.RadioId,
                replacement,
                hadPreviousPolicy: true,
                current);
            m_logger.LogWarning(
                "Radio transmit policy changed for {RadioId} to {State}",
                normalized.RadioId,
                normalizedState);
            return replacement;
        }
    }

    private RadioTransmitPreflightSnapshot? ValidateTransition(
        RadioOnboardingIdentity identity,
        string state,
        RadioTransmitPreflightSnapshot? preflight)
    {
        if (state is RadioTransmitPolicyStates.ReceiveOnly or
            RadioTransmitPolicyStates.TemporarilyDisabled)
        {
            if (preflight is not null)
            {
                throw new ArgumentException(
                    "A transmit preflight is not accepted for a disabling policy.",
                    nameof(preflight));
            }
            return null;
        }

        if (preflight is null ||
            !preflight.ValidationOnly ||
            preflight.EvaluatedAt == default ||
            preflight.EvaluatedAt > m_timeProvider.GetUtcNow() ||
            !string.Equals(
                preflight.TargetRadioId.Trim(),
                identity.SourceRadioId,
                StringComparison.OrdinalIgnoreCase) ||
            preflight.MissingPrerequisites is null ||
            preflight.MissingPrerequisites.Count >
                MaximumMissingPrerequisites)
        {
            throw new ArgumentException(
                "Current validation-only evidence for the exact source radio " +
                "is required.",
                nameof(preflight));
        }

        string reason = ValidateIdentifier(
            preflight.Reason,
            nameof(preflight.Reason),
            128);
        string[] missing = preflight.MissingPrerequisites
            .Select(value => ValidateIdentifier(
                value,
                "missingPrerequisite",
                128))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (state == RadioTransmitPolicyStates.TxEligible &&
            (!preflight.Ready || missing.Length != 0) ||
            state == RadioTransmitPolicyStates.PrerequisitesFailed &&
            (preflight.Ready || missing.Length == 0))
        {
            throw new ArgumentException(
                "The transmit policy state does not match its preflight evidence.",
                nameof(preflight));
        }

        return preflight with
        {
            TargetRadioId = identity.SourceRadioId,
            Reason = reason,
            MissingPrerequisites = missing
        };
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
            file.Policies is null ||
            file.Policies.Count > MaximumPolicies)
        {
            throw new InvalidDataException(
                "The radio onboarding policy file has an unsupported format.");
        }

        foreach (RadioOnboardingPolicySnapshot policy in file.Policies)
        {
            RadioOnboardingIdentity identity;
            try
            {
                identity = NormalizeIdentity(new(
                    policy.RadioId,
                    policy.Source,
                    policy.StationId,
                    policy.SourceRadioId));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "The radio onboarding policy file contains an invalid " +
                    "radio identity.",
                    exception);
            }

            string label = ValidateLoadedIdentifier(
                policy.Label,
                nameof(policy.Label),
                64);
            string updatedBy = ValidateLoadedIdentifier(
                policy.UpdatedBy,
                nameof(policy.UpdatedBy),
                256);
            if (!policy.Onboarded ||
                policy.UpdatedAt is not DateTimeOffset updatedAt ||
                updatedAt == default ||
                updatedAt > m_timeProvider.GetUtcNow() ||
                !RadioTransmitPolicyStates.TryNormalize(
                    policy.TransmitPolicyState,
                    out string normalizedState))
            {
                throw new InvalidDataException(
                    $"Radio '{identity.RadioId}' has invalid onboarding state.");
            }

            RadioTransmitPreflightSnapshot? preflight;
            try
            {
                preflight = ValidateTransition(
                    identity,
                    normalizedState,
                    policy.TransmitPreflight);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Radio '{identity.RadioId}' has invalid preflight evidence.",
                    exception);
            }

            RadioOnboardingPolicySnapshot normalized = policy with
            {
                RadioId = identity.RadioId,
                Source = identity.Source,
                StationId = identity.StationId,
                SourceRadioId = identity.SourceRadioId,
                Label = label,
                TransmitPolicyState = normalizedState,
                TransmitPreflight = preflight,
                UpdatedBy = updatedBy
            };
            if (!m_policies.TryAdd(identity.RadioId, normalized))
            {
                throw new InvalidDataException(
                    "The radio onboarding policy file contains duplicate radios.");
            }
        }
    }

    private void ReplaceAndPersist(
        string radioId,
        RadioOnboardingPolicySnapshot replacement,
        bool hadPreviousPolicy,
        RadioOnboardingPolicySnapshot? previousPolicy)
    {
        m_policies[radioId] = replacement;
        try
        {
            Persist();
        }
        catch
        {
            if (hadPreviousPolicy)
            {
                m_policies[radioId] = previousPolicy!;
            }
            else
            {
                m_policies.Remove(radioId);
            }
            throw;
        }
    }

    private void EnsureCapacity(bool replacingExisting)
    {
        if (!replacingExisting && m_policies.Count >= MaximumPolicies)
        {
            throw new InvalidOperationException(
                "The radio onboarding policy inventory is full.");
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

    internal static RadioOnboardingIdentity NormalizeIdentity(
        RadioOnboardingIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string radioId = ValidateIdentifier(
            identity.RadioId,
            nameof(identity.RadioId),
            128);
        string source = ValidateIdentifier(
                identity.Source,
                nameof(identity.Source),
                16)
            .ToLowerInvariant();
        if (source is not ("local" or "remote"))
        {
            throw new ArgumentException(
                "Radio source must be local or remote.",
                nameof(identity));
        }

        string stationId = identity.StationId?.Trim() ?? string.Empty;
        if (stationId.Length > 128 ||
            stationId.Any(char.IsControl) ||
            source == "remote" && stationId.Length == 0 ||
            source == "local" && stationId.Length != 0)
        {
            throw new ArgumentException(
                "The station owner does not match the radio source.",
                nameof(identity));
        }

        string sourceRadioId = ValidateIdentifier(
            identity.SourceRadioId,
            nameof(identity.SourceRadioId),
            128);
        return new(radioId, source, stationId, sourceRadioId);
    }

    private static bool IdentityMatches(
        RadioOnboardingPolicySnapshot policy,
        RadioOnboardingIdentity identity) =>
        string.Equals(
            policy.RadioId,
            identity.RadioId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            policy.Source,
            identity.Source,
            StringComparison.Ordinal) &&
        string.Equals(
            policy.StationId,
            identity.StationId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            policy.SourceRadioId,
            identity.SourceRadioId,
            StringComparison.OrdinalIgnoreCase);

    private static RadioOnboardingPolicySnapshot DefaultPolicy(
        RadioOnboardingIdentity identity) =>
        new(
            identity.RadioId,
            identity.Source,
            identity.StationId,
            identity.SourceRadioId,
            null,
            RadioTransmitPolicyStates.ReceiveOnly,
            Onboarded: false,
            TransmitPreflight: null,
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
