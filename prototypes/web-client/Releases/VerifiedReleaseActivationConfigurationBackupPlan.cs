using System.Collections.ObjectModel;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationConfigurationBackupPlanFailureCode
{
    None = 0,
    ActivationPlanNotEligible = 1,
    ActivationPlanUnavailable = 2,
    ActivationPlanMismatch = 3,
    InstallationPathsInvalid = 4,
    ReleaseRootMismatch = 5,
    BackupLayoutUnsafe = 6
}

public sealed record VerifiedReleaseActivationConfigurationBackupPlanReport(
    bool Succeeded,
    VerifiedReleaseActivationConfigurationBackupPlanFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int SourceDirectoryCount,
    bool ConfigurationDirectoryIncluded,
    bool StateDirectoryIncluded,
    bool SecretDirectoryIncluded,
    bool BackupRootSeparated,
    bool ExactActivationPlanBound,
    bool BackupManifestRequired,
    bool AtomicPublicationRequired,
    bool SourceReadPerformed,
    bool BackupWritePerformed,
    bool ExistingBackupOverwritten,
    bool ConfigurationBackupReady,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationConfigurationBackupPlan? Plan { get; init; }

    internal static VerifiedReleaseActivationConfigurationBackupPlanReport Failure(
        VerifiedReleaseActivationConfigurationBackupPlanFailureCode failureCode,
        string message,
        VerifiedReleaseActivationPlanCompositionResult? activationPlan = null) =>
        new(
            false,
            failureCode,
            message,
            activationPlan?.SetupRevision,
            activationPlan?.InstalledReleaseIdentity ?? string.Empty,
            activationPlan?.TargetReleaseIdentity ?? string.Empty,
            SourceDirectoryCount: 0,
            ConfigurationDirectoryIncluded: false,
            StateDirectoryIncluded: false,
            SecretDirectoryIncluded: false,
            BackupRootSeparated: false,
            ExactActivationPlanBound: false,
            BackupManifestRequired: true,
            AtomicPublicationRequired: true,
            SourceReadPerformed: false,
            BackupWritePerformed: false,
            ExistingBackupOverwritten: false,
            ConfigurationBackupReady: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationConfigurationBackupPlanReport Success(
        VerifiedReleaseActivationConfigurationBackupPlan plan) =>
        new(
            true,
            VerifiedReleaseActivationConfigurationBackupPlanFailureCode.None,
            "A verified configuration-backup plan was composed without reading or mutating installation state.",
            plan.ActivationPlan.SetupRevision,
            plan.ActivationPlan.InstalledReleaseIdentity,
            plan.ActivationPlan.TargetReleaseIdentity,
            plan.Sources.Count,
            ConfigurationDirectoryIncluded: true,
            StateDirectoryIncluded: true,
            SecretDirectoryIncluded: true,
            BackupRootSeparated: true,
            ExactActivationPlanBound: true,
            BackupManifestRequired: true,
            AtomicPublicationRequired: true,
            SourceReadPerformed: false,
            BackupWritePerformed: false,
            ExistingBackupOverwritten: false,
            ConfigurationBackupReady: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            Plan = plan
        };
}

public sealed record VerifiedReleaseActivationConfigurationBackupPlanDiagnostics(
    bool Registered,
    bool ActivationPlanInputRegistered,
    bool InstallationPathsInputRegistered,
    bool ExactActivationPlanBindingRegistered,
    bool ConfigurationSourcePlanningRegistered,
    bool StateSourcePlanningRegistered,
    bool SecretSourcePlanningRegistered,
    bool ReleaseRootAgreementRegistered,
    bool BackupRootSeparationRegistered,
    bool BackupIdentityPlanningRegistered,
    bool BackupManifestPlanningRegistered,
    bool AtomicPublicationPlanningRegistered,
    bool SourceReadRegistered,
    bool FileWriteRegistered,
    bool DirectoryMutationRegistered,
    bool ExistingBackupOverwriteRegistered,
    bool BackupExecutionRegistered,
    bool ConfigurationBackupEvidenceRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationAuthorityRegistered,
    bool OperationalCallerRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool HttpCallerRegistered,
    bool WebSocketCallerRegistered,
    bool HostedServiceCallerRegistered,
    bool TimerCallerRegistered,
    bool AetherRemoteCallerRegistered,
    bool ServiceControlCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal enum VerifiedReleaseActivationConfigurationBackupSourceKind
{
    Configuration = 1,
    State = 2,
    Secret = 3
}

internal sealed record VerifiedReleaseActivationConfigurationBackupSourcePlan(
    VerifiedReleaseActivationConfigurationBackupSourceKind Kind,
    string SourcePath,
    string StagedPath);

internal sealed class VerifiedReleaseActivationConfigurationBackupPlan
{
    private readonly ReadOnlyCollection<
        VerifiedReleaseActivationConfigurationBackupSourcePlan> m_sources;

    internal VerifiedReleaseActivationConfigurationBackupPlan(
        VerifiedReleaseActivationPlan activationPlan,
        string backupRootPath,
        string stagingPath,
        string publishedPath,
        string manifestPath,
        IReadOnlyList<VerifiedReleaseActivationConfigurationBackupSourcePlan>
            sources)
    {
        ActivationPlan = activationPlan ??
            throw new ArgumentNullException(nameof(activationPlan));
        BackupRootPath = backupRootPath;
        StagingPath = stagingPath;
        PublishedPath = publishedPath;
        ManifestPath = manifestPath;
        m_sources = Array.AsReadOnly(
            (sources ?? throw new ArgumentNullException(nameof(sources)))
                .ToArray());
    }

    internal VerifiedReleaseActivationPlan ActivationPlan { get; }
    internal string BackupRootPath { get; }
    internal string StagingPath { get; }
    internal string PublishedPath { get; }
    internal string ManifestPath { get; }
    internal IReadOnlyList<VerifiedReleaseActivationConfigurationBackupSourcePlan>
        Sources => m_sources;
    internal bool ExistingBackupOverwriteAllowed => false;
    internal bool AtomicPublicationRequired => true;
}

/// <summary>
/// Pure fail-closed composition of one exact verified activation plan into a
/// future configuration-backup transaction plan. The plan binds the dedicated
/// configuration, state, and secret roots to a canonical non-overlapping backup
/// location and requires a manifest plus atomic publication. It does not inspect
/// source contents, create a staging tree, write a backup, overwrite an existing
/// backup, mutate current, authorize activation, call a service, touch a radio or
/// watchdog, or expose an operational caller.
/// </summary>
public sealed class VerifiedReleaseActivationConfigurationBackupPlanner
{
    private const int ExpectedSourceCount = 3;
    private readonly InstallationPaths m_paths;

    public VerifiedReleaseActivationConfigurationBackupPlanner(
        InstallationPaths paths)
    {
        m_paths = paths ?? throw new ArgumentNullException(nameof(paths));
        Snapshot =
            new VerifiedReleaseActivationConfigurationBackupPlanDiagnostics(
                Registered: true,
                ActivationPlanInputRegistered: true,
                InstallationPathsInputRegistered: true,
                ExactActivationPlanBindingRegistered: true,
                ConfigurationSourcePlanningRegistered: true,
                StateSourcePlanningRegistered: true,
                SecretSourcePlanningRegistered: true,
                ReleaseRootAgreementRegistered: true,
                BackupRootSeparationRegistered: true,
                BackupIdentityPlanningRegistered: true,
                BackupManifestPlanningRegistered: true,
                AtomicPublicationPlanningRegistered: true,
                SourceReadRegistered: false,
                FileWriteRegistered: false,
                DirectoryMutationRegistered: false,
                ExistingBackupOverwriteRegistered: false,
                BackupExecutionRegistered: false,
                ConfigurationBackupEvidenceRegistered: false,
                CurrentPointerMutationRegistered: false,
                ActivationAuthorityRegistered: false,
                OperationalCallerRegistered: false,
                CliCallerRegistered: false,
                AdminCallerRegistered: false,
                BrowserCallerRegistered: false,
                HttpCallerRegistered: false,
                WebSocketCallerRegistered: false,
                HostedServiceCallerRegistered: false,
                TimerCallerRegistered: false,
                AetherRemoteCallerRegistered: false,
                ServiceControlCallerRegistered: false,
                RadioCallerRegistered: false,
                WatchdogCallerRegistered: false,
                CommandCallerRegistered: false,
                LeaseCallerRegistered: false,
                TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationConfigurationBackupPlanDiagnostics Snapshot
    {
        get;
    }

    internal VerifiedReleaseActivationConfigurationBackupPlanReport Compose(
        VerifiedReleaseActivationPlanCompositionResult activationPlanResult)
    {
        ArgumentNullException.ThrowIfNull(activationPlanResult);

        if (!IsEligibleActivationPlan(activationPlanResult))
        {
            return VerifiedReleaseActivationConfigurationBackupPlanReport.Failure(
                VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                    .ActivationPlanNotEligible,
                "A successful non-mutating verified activation plan is required.",
                activationPlanResult);
        }

        VerifiedReleaseActivationPlan? activationPlan = activationPlanResult.Plan;
        if (activationPlan is null)
        {
            return VerifiedReleaseActivationConfigurationBackupPlanReport.Failure(
                VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                    .ActivationPlanUnavailable,
                "The successful activation plan does not retain its internal verified plan.",
                activationPlanResult);
        }
        if (!MatchesActivationPlan(activationPlanResult, activationPlan))
        {
            return VerifiedReleaseActivationConfigurationBackupPlanReport.Failure(
                VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                    .ActivationPlanMismatch,
                "Activation plan metadata does not match its public summary.",
                activationPlanResult);
        }

        InstallationPaths paths;
        try
        {
            paths = NormalizeAndValidateInstallationPaths(m_paths);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return VerifiedReleaseActivationConfigurationBackupPlanReport.Failure(
                VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                    .InstallationPathsInvalid,
                "Installation paths cannot produce a canonical configuration-backup plan.",
                activationPlanResult);
        }

        if (!PathEquals(paths.ReleaseDirectory, activationPlan.ReleaseRootPath))
        {
            return VerifiedReleaseActivationConfigurationBackupPlanReport.Failure(
                VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                    .ReleaseRootMismatch,
                "The activation release root does not match the resolved installation layout.",
                activationPlanResult);
        }

        try
        {
            if (!ValidateSeparatedLayout(paths, activationPlan))
            {
                throw new InvalidOperationException(
                    "The installation and backup roots overlap.");
            }

            string activationBackupRoot = DirectChild(
                paths.BackupDirectory,
                "activation");
            string revisionRoot = DirectChild(
                activationBackupRoot,
                $"setup-{activationPlan.SetupRevision}");
            string backupIdentity =
                $"{activationPlan.InstalledReleaseIdentity}-to-" +
                activationPlan.TargetReleaseIdentity;
            string publishedPath = DirectChild(revisionRoot, backupIdentity);
            string stagingPath = DirectChild(
                revisionRoot,
                $".{backupIdentity}.staging");
            string manifestPath = DirectChild(
                publishedPath,
                "backup-manifest.json");

            VerifiedReleaseActivationConfigurationBackupSourcePlan[] sources =
            [
                new(
                    VerifiedReleaseActivationConfigurationBackupSourceKind
                        .Configuration,
                    paths.ConfigurationDirectory,
                    DirectChild(stagingPath, "configuration")),
                new(
                    VerifiedReleaseActivationConfigurationBackupSourceKind.State,
                    paths.StateDirectory,
                    DirectChild(stagingPath, "state")),
                new(
                    VerifiedReleaseActivationConfigurationBackupSourceKind.Secret,
                    paths.SecretDirectory,
                    DirectChild(stagingPath, "secrets"))
            ];
            if (sources.Length != ExpectedSourceCount ||
                sources.Select(source => source.Kind).Distinct().Count() !=
                    ExpectedSourceCount ||
                sources.Select(source => source.SourcePath)
                    .Distinct(PathComparer).Count() != ExpectedSourceCount ||
                sources.Select(source => source.StagedPath)
                    .Distinct(PathComparer).Count() != ExpectedSourceCount)
            {
                throw new InvalidOperationException(
                    "The backup source plan is incomplete or duplicated.");
            }

            return VerifiedReleaseActivationConfigurationBackupPlanReport.Success(
                new VerifiedReleaseActivationConfigurationBackupPlan(
                    activationPlan,
                    paths.BackupDirectory,
                    stagingPath,
                    publishedPath,
                    manifestPath,
                    sources));
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException or OverflowException)
        {
            return VerifiedReleaseActivationConfigurationBackupPlanReport.Failure(
                VerifiedReleaseActivationConfigurationBackupPlanFailureCode
                    .BackupLayoutUnsafe,
                "The resolved installation layout cannot produce a separated bounded backup transaction.",
                activationPlanResult);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static InstallationPaths NormalizeAndValidateInstallationPaths(
        InstallationPaths paths)
    {
        InstallationPaths normalized = new(
            CanonicalDirectory(paths.ConfigurationDirectory),
            CanonicalDirectory(paths.StateDirectory),
            CanonicalDirectory(paths.SecretDirectory),
            CanonicalDirectory(paths.ReleaseDirectory),
            CanonicalDirectory(paths.BackupDirectory),
            CanonicalDirectory(paths.LogDirectory));
        InstallationPaths.Validate(normalized);
        if (!string.Equals(
                paths.ConfigurationDirectory,
                normalized.ConfigurationDirectory,
                PathComparison) ||
            !string.Equals(
                paths.StateDirectory,
                normalized.StateDirectory,
                PathComparison) ||
            !string.Equals(
                paths.SecretDirectory,
                normalized.SecretDirectory,
                PathComparison) ||
            !string.Equals(
                paths.ReleaseDirectory,
                normalized.ReleaseDirectory,
                PathComparison) ||
            !string.Equals(
                paths.BackupDirectory,
                normalized.BackupDirectory,
                PathComparison) ||
            !string.Equals(
                paths.LogDirectory,
                normalized.LogDirectory,
                PathComparison))
        {
            throw new InvalidOperationException(
                "Installation paths must already be canonical.");
        }
        return normalized;
    }

    private static bool ValidateSeparatedLayout(
        InstallationPaths paths,
        VerifiedReleaseActivationPlan activationPlan)
    {
        string[] roots =
        [
            paths.ConfigurationDirectory,
            paths.StateDirectory,
            paths.SecretDirectory,
            paths.ReleaseDirectory,
            paths.BackupDirectory,
            paths.LogDirectory
        ];
        for (int left = 0; left < roots.Length; left++)
        {
            if (IsFileSystemRoot(roots[left]))
            {
                return false;
            }
            for (int right = left + 1; right < roots.Length; right++)
            {
                if (PathsOverlap(roots[left], roots[right]))
                {
                    return false;
                }
            }
        }

        string deploymentRoot = CanonicalDirectory(
            activationPlan.DeploymentRootPath);
        if (IsFileSystemRoot(deploymentRoot) ||
            !PathEquals(
                Path.GetDirectoryName(paths.ReleaseDirectory),
                deploymentRoot))
        {
            return false;
        }

        string[] nonReleaseRoots =
        [
            paths.ConfigurationDirectory,
            paths.StateDirectory,
            paths.SecretDirectory,
            paths.BackupDirectory,
            paths.LogDirectory
        ];
        return nonReleaseRoots.All(root => !PathsOverlap(root, deploymentRoot));
    }

    private static bool IsEligibleActivationPlan(
        VerifiedReleaseActivationPlanCompositionResult result) =>
        result.Succeeded &&
        result.FailureCode == VerifiedReleaseActivationPlanFailureCode.None &&
        result.SetupRevision is > 0 &&
        result.PackageCount == 4 &&
        result.PublishedBytes > 0 &&
        result.TxLeaseAdmissionClosureRequired &&
        result.RadioAuthoritativeIdleRequired &&
        result.WatchdogsDisarmedRequired &&
        result.ConfigurationBackupRequired &&
        result.AtomicCurrentPointerSwitchRequired &&
        result.ServiceHealthVerificationRequired &&
        result.AutomaticRollbackRequired &&
        result.OperatorApprovalRequired &&
        !result.CurrentPointerMutationPerformed &&
        !result.ActivationPerformed;

    private static bool MatchesActivationPlan(
        VerifiedReleaseActivationPlanCompositionResult result,
        VerifiedReleaseActivationPlan plan) =>
        result.SetupRevision == plan.SetupRevision &&
        string.Equals(
            result.InstalledReleaseIdentity,
            plan.InstalledReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            result.TargetReleaseIdentity,
            plan.TargetReleaseIdentity,
            StringComparison.Ordinal) &&
        string.Equals(result.TargetVersion, plan.TargetVersion, StringComparison.Ordinal) &&
        result.Architecture == plan.Architecture &&
        result.PackageCount == plan.Packages.Count &&
        result.TargetConfigurationSchemaVersion ==
            plan.TargetConfigurationSchemaVersion &&
        result.MigrationKind == plan.MigrationKind &&
        result.MigrationRequired == plan.MigrationRequired &&
        result.RestartServiceCount == plan.RestartServiceCount &&
        result.HostRestartRequired == plan.RestartHost &&
        plan.ConfigurationBackupRequired &&
        plan.TxLeaseAdmissionClosureRequired &&
        plan.RadioAuthoritativeIdleRequired &&
        plan.WatchdogsDisarmedRequired &&
        plan.AtomicCurrentPointerSwitchRequired &&
        plan.ServiceHealthVerificationRequired &&
        plan.AutomaticRollbackRequired &&
        plan.OperatorApprovalRequired;

    private static string CanonicalDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
        {
            throw new InvalidOperationException(
                "A canonical absolute directory is required.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static string DirectChild(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(child) ||
            child is "." or ".." ||
            child.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException(
                "A safe direct child name is required.");
        }

        string canonicalParent = CanonicalDirectory(parent);
        string result = Path.GetFullPath(Path.Combine(canonicalParent, child));
        if (!PathEquals(Path.GetDirectoryName(result), canonicalParent))
        {
            throw new InvalidOperationException(
                "The planned path escaped its parent directory.");
        }
        return result;
    }

    private static bool IsFileSystemRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && PathEquals(path, root);
    }

    private static bool PathsOverlap(string left, string right) =>
        IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        if (PathEquals(candidate, parent))
        {
            return true;
        }

        string prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);
    }
}
