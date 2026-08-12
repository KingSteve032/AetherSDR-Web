using System.Text.Json.Serialization;

namespace AetherSDR.Web.Setup;

public enum InstallationPathLayout
{
    Development = 1,
    LinuxSystem = 2
}

public sealed class InstallationPathSettings
{
    public const string SectionName = "InstallationPaths";

    public string ConfigurationDirectory { get; init; } = string.Empty;
    public string StateDirectory { get; init; } = string.Empty;
    public string SecretDirectory { get; init; } = string.Empty;
    public string ReleaseDirectory { get; init; } = string.Empty;
    public string BackupDirectory { get; init; } = string.Empty;
    public string LogDirectory { get; init; } = string.Empty;
}

public sealed record InstallationPaths(
    string ConfigurationDirectory,
    string StateDirectory,
    string SecretDirectory,
    string ReleaseDirectory,
    string BackupDirectory,
    string LogDirectory)
{
    [JsonIgnore]
    public string ConfigurationFilePath =>
        Path.Combine(ConfigurationDirectory, "aethersdr.json");

    [JsonIgnore]
    public string SetupStatePath =>
        Path.Combine(StateDirectory, "setup", "installation.json");

    [JsonIgnore]
    public string DataProtectionKeyDirectory =>
        Path.Combine(SecretDirectory, "data-protection");

    [JsonIgnore]
    public string IdentityStoreDirectory =>
        Path.Combine(StateDirectory, "identity");

    [JsonIgnore]
    public string IdentityDatabasePath =>
        Path.Combine(IdentityStoreDirectory, "aethersdr-identity.db");

    [JsonIgnore]
    public string RadioAccessPolicyPath =>
        Path.Combine(StateDirectory, "radio-access", "policies.json");

    [JsonIgnore]
    public string RadioOnboardingPolicyPath =>
        Path.Combine(StateDirectory, "radio-access", "onboarding.json");

    [JsonIgnore]
    public string AdministrativeAuditPath =>
        Path.Combine(StateDirectory, "radio-access", "audit.json");

    [JsonIgnore]
    public string ReleaseDownloadDirectory =>
        Path.Combine(StateDirectory, "release-downloads");

    public static InstallationPaths Resolve(
        string contentRoot,
        InstallationPathLayout layout,
        InstallationPathSettings? settings = null)
    {
        settings ??= new InstallationPathSettings();
        string root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(contentRoot)
                ? throw new InvalidOperationException(
                    "The application content root is required to resolve installation paths.")
                : contentRoot);

        InstallationPaths defaults = layout switch
        {
            InstallationPathLayout.Development => new(
                Path.Combine(root, ".aethersdr", "config"),
                Path.Combine(root, ".aethersdr", "state"),
                Path.Combine(root, ".aethersdr", "secrets"),
                Path.Combine(root, ".aethersdr", "releases"),
                Path.Combine(root, ".aethersdr", "backups"),
                Path.Combine(root, ".aethersdr", "logs")),
            InstallationPathLayout.LinuxSystem => new(
                "/etc/aethersdr",
                "/var/lib/aethersdr",
                "/var/lib/aethersdr/secrets",
                "/opt/aethersdr/releases",
                "/var/backups/aethersdr",
                "/var/log/aethersdr"),
            _ => throw new InvalidOperationException(
                $"Unsupported installation path layout '{layout}'.")
        };

        InstallationPaths resolved = new(
            ResolveDirectory(
                settings.ConfigurationDirectory,
                defaults.ConfigurationDirectory,
                nameof(settings.ConfigurationDirectory)),
            ResolveDirectory(
                settings.StateDirectory,
                defaults.StateDirectory,
                nameof(settings.StateDirectory)),
            ResolveDirectory(
                settings.SecretDirectory,
                defaults.SecretDirectory,
                nameof(settings.SecretDirectory)),
            ResolveDirectory(
                settings.ReleaseDirectory,
                defaults.ReleaseDirectory,
                nameof(settings.ReleaseDirectory)),
            ResolveDirectory(
                settings.BackupDirectory,
                defaults.BackupDirectory,
                nameof(settings.BackupDirectory)),
            ResolveDirectory(
                settings.LogDirectory,
                defaults.LogDirectory,
                nameof(settings.LogDirectory)));
        Validate(resolved);
        return resolved;
    }

    public static void Validate(InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            [nameof(ConfigurationDirectory)] = paths.ConfigurationDirectory,
            [nameof(StateDirectory)] = paths.StateDirectory,
            [nameof(SecretDirectory)] = paths.SecretDirectory,
            [nameof(ReleaseDirectory)] = paths.ReleaseDirectory,
            [nameof(BackupDirectory)] = paths.BackupDirectory,
            [nameof(LogDirectory)] = paths.LogDirectory
        };

        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        HashSet<string> distinct = new(comparer);
        foreach ((string name, string value) in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
            {
                throw new InvalidOperationException(
                    $"Installation path {name} must be an absolute directory path.");
            }

            string normalized = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(value));
            if (!distinct.Add(normalized))
            {
                throw new InvalidOperationException(
                    "Installation configuration, state, secret, release, backup, " +
                    "and log directories must be distinct.");
            }
        }
    }

    private static string ResolveDirectory(
        string configured,
        string fallback,
        string name)
    {
        string value = string.IsNullOrWhiteSpace(configured)
            ? fallback
            : configured.Trim();
        if (!Path.IsPathRooted(value))
        {
            throw new InvalidOperationException(
                $"InstallationPaths:{name} must be an absolute directory path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
}
