using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed record StationTxProductionActivationPreflightReport(
    int Version,
    bool ValidationOnly,
    bool WebHostStarted,
    bool RadioConnectionCreated,
    bool WatchdogProcessStarted,
    string TargetRadioId,
    bool ActivationCurrentlyRequested,
    bool CandidateConfigurationValid,
    bool CandidatePlanAvailable,
    bool CandidateBindingApplied,
    bool TrustedKeyMaterialReady,
    int TrustedKeyCount,
    bool SigningKeyMaterialReady,
    string? SigningKeyId,
    string? SigningKeyFingerprint,
    bool SigningKeyTrusted,
    bool PrimaryTransportRadioAllowed,
    bool EmergencyTransportRadioAllowed,
    bool WatchdogRadioAllowed,
    bool WatchdogExecutableReady,
    bool ReadyForOperatorActivation,
    string Reason,
    IReadOnlyList<string> MissingPrerequisites);

/// <summary>
/// Static, non-starting production TX activation preflight. It validates the
/// exact configuration and key material that would be used by one reviewed
/// local radio, but never builds the web host, opens a radio connection, starts
/// the independent watchdog, acquires a lease, or exposes a command caller.
/// </summary>
internal static class StationTxProductionActivationPreflight
{
    internal const int Version = 1;

    public static StationTxProductionActivationPreflightReport Evaluate(
        string targetRadioId,
        string contentRootPath,
        StationTxProductionActivationSettings activation,
        RadioSettings radio,
        StationTxCommandTrustSettings commandTrust,
        StationTxCommandSigningSettings commandSigning,
        StationTxCommandEnvelopeCoordinatorSettings commandCoordinator,
        StationTxCommandTransportSettings commandTransport,
        StationTxEmergencyUnkeyTransportSettings emergencyUnkeyTransport,
        IndependentTxWatchdogSettings watchdog)
    {
        ArgumentNullException.ThrowIfNull(contentRootPath);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(commandTrust);
        ArgumentNullException.ThrowIfNull(commandSigning);
        ArgumentNullException.ThrowIfNull(commandCoordinator);
        ArgumentNullException.ThrowIfNull(commandTransport);
        ArgumentNullException.ThrowIfNull(emergencyUnkeyTransport);
        ArgumentNullException.ThrowIfNull(watchdog);

        string normalizedRadioId = NormalizeRadioId(targetRadioId);
        List<string> missing = [];

        StationTxProductionActivationConfigurationDiagnostics candidate =
            StationTxProductionActivationConfigurationInterlock.Evaluate(
                StationTxProductionActivationConfigurationInterlock.CreateInputs(
                    new StationTxProductionActivationSettings { Enabled = true },
                    radio,
                    commandTrust,
                    commandSigning,
                    commandCoordinator,
                    commandTransport,
                    emergencyUnkeyTransport,
                    watchdog));
        AddRange(missing, candidate.MissingPrerequisites);

        StationTxCommandTrustDiagnostics? trustDiagnostics = null;
        try
        {
            using StationTxCommandTrustRegistry registry = new(
                Options.Create(commandTrust),
                NullLogger<StationTxCommandTrustRegistry>.Instance);
            trustDiagnostics = registry.Snapshot;
        }
        catch
        {
            AddMissing(missing, "command-trust-material-invalid");
        }

        StationTxCommandSigningDiagnostics? signingDiagnostics = null;
        try
        {
            using StationTxCommandSigningAuthority authority = new(
                Options.Create(commandSigning),
                NullLogger<StationTxCommandSigningAuthority>.Instance);
            signingDiagnostics = authority.Snapshot;
        }
        catch
        {
            AddMissing(missing, "command-signing-material-invalid");
        }

        bool primaryAllowed = false;
        try
        {
            StationTxCommandTransportConfiguration configuration =
                StationTxCommandTransportSettingsValidator.Validate(
                    commandTransport);
            primaryAllowed = configuration.IsRadioAllowed(normalizedRadioId);
            if (!primaryAllowed)
            {
                AddMissing(missing, "command-transport-radio-not-allowed");
            }
        }
        catch
        {
            AddMissing(missing, "command-transport-configuration-invalid");
        }

        bool emergencyAllowed = false;
        try
        {
            StationTxEmergencyUnkeyTransportConfiguration configuration =
                StationTxEmergencyUnkeyTransportSettingsValidator.Validate(
                    emergencyUnkeyTransport);
            emergencyAllowed = configuration.IsRadioAllowed(normalizedRadioId);
            if (!emergencyAllowed)
            {
                AddMissing(
                    missing,
                    "emergency-unkey-transport-radio-not-allowed");
            }
        }
        catch
        {
            AddMissing(
                missing,
                "emergency-unkey-transport-configuration-invalid");
        }

        IndependentTxWatchdogSettings validatedWatchdog = Clone(watchdog);
        bool watchdogAllowed = false;
        bool watchdogExecutableReady = false;
        try
        {
            validatedWatchdog = StationTxIndependentWatchdogRegistry.Validate(
                validatedWatchdog);
            watchdogAllowed = validatedWatchdog.AllowedRadioIds.Contains(
                normalizedRadioId,
                StringComparer.Ordinal);
            if (!watchdogAllowed)
            {
                AddMissing(missing, "watchdog-radio-not-allowed");
            }

            watchdogExecutableReady = ValidateWatchdogExecutable(
                validatedWatchdog,
                contentRootPath);
            if (!watchdogExecutableReady)
            {
                AddMissing(missing, "watchdog-executable-unavailable");
            }
        }
        catch
        {
            AddMissing(missing, "watchdog-configuration-invalid");
        }

        if (!IPAddress.TryParse(radio.Host, out IPAddress? radioAddress) ||
            radioAddress.AddressFamily != AddressFamily.InterNetwork ||
            radio.TcpPort is < 1 or > 65535)
        {
            AddMissing(missing, "radio-endpoint-invalid");
        }

        bool trustedKeyMaterialReady =
            trustDiagnostics?.SignatureVerificationAvailable == true;
        if (!trustedKeyMaterialReady)
        {
            AddMissing(missing, "command-trust-material-unavailable");
        }

        bool signingKeyMaterialReady =
            signingDiagnostics?.SigningAvailable == true;
        if (!signingKeyMaterialReady)
        {
            AddMissing(missing, "command-signing-material-unavailable");
        }

        bool signingKeyTrusted =
            signingDiagnostics is not null &&
            signingDiagnostics.SigningAvailable &&
            trustDiagnostics is not null &&
            trustDiagnostics.SignatureVerificationAvailable &&
            trustDiagnostics.TrustedKeys.Any(key =>
                string.Equals(
                    key.KeyId,
                    signingDiagnostics.KeyId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    key.Fingerprint,
                    signingDiagnostics.PublicKeyFingerprint,
                    StringComparison.Ordinal));
        if (!signingKeyTrusted)
        {
            AddMissing(missing, "command-signing-key-not-trusted");
        }

        StationTxProductionActivationPlanDiagnostics plan =
            new StationTxProductionActivationPlanner(() => candidate).Snapshot;
        StationTxProductionActivationBindingDiagnostics binding =
            StationTxProductionActivationBinder.Bind(
                plan,
                localFlexSessionEligible: string.Equals(
                    radio.Mode,
                    "FlexRx",
                    StringComparison.OrdinalIgnoreCase),
                allowTransmitConfigured: radio.AllowTransmit,
                browserTxLeaseConfigured: radio.BrowserTxLeaseEnabled);

        bool ready =
            candidate.ConfigurationValid &&
            plan.PlanAvailable &&
            binding.BindingApplied &&
            trustedKeyMaterialReady &&
            signingKeyMaterialReady &&
            signingKeyTrusted &&
            primaryAllowed &&
            emergencyAllowed &&
            watchdogAllowed &&
            watchdogExecutableReady &&
            missing.Count == 0;
        string reason = ready ? "preflight-ready" : missing[0];

        return new StationTxProductionActivationPreflightReport(
            Version,
            ValidationOnly: true,
            WebHostStarted: false,
            RadioConnectionCreated: false,
            WatchdogProcessStarted: false,
            normalizedRadioId,
            ActivationCurrentlyRequested: activation.Enabled,
            CandidateConfigurationValid: candidate.ConfigurationValid,
            CandidatePlanAvailable: plan.PlanAvailable,
            CandidateBindingApplied: binding.BindingApplied,
            TrustedKeyMaterialReady: trustedKeyMaterialReady,
            TrustedKeyCount: trustDiagnostics?.TrustedKeyCount ?? 0,
            SigningKeyMaterialReady: signingKeyMaterialReady,
            SigningKeyId: signingDiagnostics?.KeyId,
            SigningKeyFingerprint: signingDiagnostics?.PublicKeyFingerprint,
            signingKeyTrusted,
            primaryAllowed,
            emergencyAllowed,
            watchdogAllowed,
            watchdogExecutableReady,
            ReadyForOperatorActivation: ready,
            reason,
            missing.ToArray());
    }

    private static IndependentTxWatchdogSettings Clone(
        IndependentTxWatchdogSettings settings) =>
        new()
        {
            Enabled = settings.Enabled,
            ExecutablePath = settings.ExecutablePath,
            RequestTimeoutMilliseconds = settings.RequestTimeoutMilliseconds,
            RestartDelayMilliseconds = settings.RestartDelayMilliseconds,
            RadioCommandTransportEnabled =
                settings.RadioCommandTransportEnabled,
            ArmingEnabled = settings.ArmingEnabled,
            AllowedRadioIds = [.. settings.AllowedRadioIds ?? []],
            RadioCommandTimeoutMilliseconds =
                settings.RadioCommandTimeoutMilliseconds
        };

    private static bool ValidateWatchdogExecutable(
        IndependentTxWatchdogSettings settings,
        string contentRootPath)
    {
        string configured = settings.ExecutablePath.Trim();
        string executable = configured.Length > 0
            ? Path.GetFullPath(configured, contentRootPath)
            : Path.Combine(
                AppContext.BaseDirectory,
                "watchdog",
                OperatingSystem.IsWindows()
                    ? "AetherSDR.TxWatchdog.exe"
                    : "AetherSDR.TxWatchdog");
        string expectedFileName = OperatingSystem.IsWindows()
            ? "AetherSDR.TxWatchdog.exe"
            : "AetherSDR.TxWatchdog";
        if (!string.Equals(
                Path.GetFileName(executable),
                expectedFileName,
                StringComparison.Ordinal) ||
            !File.Exists(executable) ||
            File.GetAttributes(executable).HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        UnixFileMode mode = File.GetUnixFileMode(executable);
        return mode.HasFlag(UnixFileMode.UserExecute) &&
            !mode.HasFlag(UnixFileMode.GroupWrite) &&
            !mode.HasFlag(UnixFileMode.OtherWrite);
    }

    private static string NormalizeRadioId(string? value)
    {
        string normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length is 0 or > 128 ||
            normalized.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "The production TX preflight requires one canonical exact radio ID.");
        }
        return normalized;
    }

    private static void AddRange(
        ICollection<string> target,
        IEnumerable<string> values)
    {
        foreach (string value in values)
        {
            AddMissing(target, value);
        }
    }

    private static void AddMissing(ICollection<string> missing, string reason)
    {
        if (!missing.Contains(reason, StringComparer.Ordinal))
        {
            missing.Add(reason);
        }
    }
}
