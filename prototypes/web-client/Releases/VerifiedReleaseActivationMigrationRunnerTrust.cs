using System.Collections.ObjectModel;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseMigrationRunnerTrustSettings
{
    public const string SectionName = "ReleaseMigrationRunnerTrust";

    public bool SelectionEnabled { get; set; }
    public ReleaseMigrationRunnerTrustEntrySettings[] Runners { get; set; } = [];
}

public sealed class ReleaseMigrationRunnerTrustEntrySettings
{
    public string RunnerIdentity { get; set; } = string.Empty;
    public int RunnerProtocolVersion { get; set; }
    public string RunnerPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public ReleaseMigrationRunnerTrustMappingSettings[] Migrations { get; set; } = [];
}

public sealed class ReleaseMigrationRunnerTrustMappingSettings
{
    public string MigrationIdentity { get; set; } = string.Empty;
    public int FromConfigurationSchemaVersion { get; set; }
    public int ToConfigurationSchemaVersion { get; set; }
}

public sealed record ReleaseMigrationRunnerTrustDiagnostics(
    bool Registered,
    bool SelectionEnabled,
    bool SelectionAvailable,
    int TrustedRunnerCount,
    int TrustedMigrationCount,
    bool FeatureOwnedConfigurationRegistered,
    bool BoundedRunnerListRegistered,
    bool BoundedMigrationListRegistered,
    bool CanonicalRunnerPathValidationRegistered,
    bool SymbolicLinkRejectionRegistered,
    bool RunnerSizeValidationRegistered,
    bool RunnerPermissionValidationRegistered,
    bool RunnerDigestPinningRegistered,
    bool ExactMigrationMappingRegistered,
    bool RunnerArtifactReadRegistered,
    bool RunnerInvocationRegistered,
    bool MigrationExecutionRegistered,
    bool MigrationEvidenceRegistered,
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
    bool HealthProbeCallerRegistered,
    bool RollbackCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered,
    string Reason);

internal sealed record ReleaseMigrationRunnerMapping(
    string MigrationIdentity,
    int FromConfigurationSchemaVersion,
    int ToConfigurationSchemaVersion);

internal sealed class ReleaseMigrationTrustedRunner
{
    private readonly ReadOnlyCollection<ReleaseMigrationRunnerMapping> m_migrations;
    private readonly ReadOnlyCollection<byte> m_sha256;

    internal ReleaseMigrationTrustedRunner(
        string runnerIdentity,
        int runnerProtocolVersion,
        string runnerPath,
        long runnerLength,
        byte[] sha256,
        DateTime lastWriteTimeUtc,
        IReadOnlyList<ReleaseMigrationRunnerMapping> migrations)
    {
        RunnerIdentity = runnerIdentity;
        RunnerProtocolVersion = runnerProtocolVersion;
        RunnerPath = runnerPath;
        RunnerLength = runnerLength;
        m_sha256 = Array.AsReadOnly(
            (sha256 ?? throw new ArgumentNullException(nameof(sha256))).ToArray());
        LastWriteTimeUtc = lastWriteTimeUtc;
        m_migrations = Array.AsReadOnly(
            (migrations ?? throw new ArgumentNullException(nameof(migrations)))
                .ToArray());
    }

    internal string RunnerIdentity { get; }
    internal int RunnerProtocolVersion { get; }
    internal string RunnerPath { get; }
    internal long RunnerLength { get; }
    internal IReadOnlyList<byte> Sha256 => m_sha256;
    internal DateTime LastWriteTimeUtc { get; }
    internal IReadOnlyList<ReleaseMigrationRunnerMapping> Migrations => m_migrations;
}

/// <summary>
/// Immutable disabled-by-default registry of locally reviewed migration-runner
/// artifacts and the exact signed migration declarations each artifact may
/// satisfy. Startup validates canonical paths, rejects links, bounds file and
/// mapping counts, requires immutable permissions on Linux, and pins SHA-256.
/// It never invokes a runner, reads configuration backup content, migrates data,
/// writes files, changes current, activates a release, controls services, probes
/// health, rolls back, or touches radio, watchdog, command, lease, or TX state.
/// </summary>
public sealed class ReleaseMigrationRunnerTrustRegistry
{
    internal const int CurrentRunnerProtocolVersion = 1;
    internal const int MaximumTrustedRunners = 8;
    internal const int MaximumMigrationsPerRunner = 16;
    internal const int MaximumTrustedMigrations = 64;
    internal const int MaximumRunnerIdentityLength = 64;
    internal const int MaximumMigrationIdentityLength = 96;
    internal const int MaximumRunnerPathLength = 1024;
    internal const long MaximumRunnerFileBytes = 16L * 1024 * 1024;

    private const UnixFileMode AnyWritableUnixModes =
        UnixFileMode.UserWrite |
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;
    private const UnixFileMode ForbiddenSharedWritableUnixModes =
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private readonly ReadOnlyCollection<ReleaseMigrationTrustedRunner> m_runners;

    public ReleaseMigrationRunnerTrustRegistry(
        IOptions<ReleaseMigrationRunnerTrustSettings> options,
        ILogger<ReleaseMigrationRunnerTrustRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        ReleaseMigrationRunnerTrustSettings settings = options.Value ??
            new ReleaseMigrationRunnerTrustSettings();
        ReleaseMigrationTrustedRunner[] runners = LoadRunners(settings);
        m_runners = Array.AsReadOnly(runners);
        int migrationCount = runners.Sum(runner => runner.Migrations.Count);
        bool available = settings.SelectionEnabled && migrationCount > 0;
        Snapshot = new ReleaseMigrationRunnerTrustDiagnostics(
            Registered: true,
            settings.SelectionEnabled,
            SelectionAvailable: available,
            TrustedRunnerCount: runners.Length,
            TrustedMigrationCount: migrationCount,
            FeatureOwnedConfigurationRegistered: true,
            BoundedRunnerListRegistered: true,
            BoundedMigrationListRegistered: true,
            CanonicalRunnerPathValidationRegistered: true,
            SymbolicLinkRejectionRegistered: true,
            RunnerSizeValidationRegistered: true,
            RunnerPermissionValidationRegistered: true,
            RunnerDigestPinningRegistered: true,
            ExactMigrationMappingRegistered: true,
            RunnerArtifactReadRegistered: true,
            RunnerInvocationRegistered: false,
            MigrationExecutionRegistered: false,
            MigrationEvidenceRegistered: false,
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
            HealthProbeCallerRegistered: false,
            RollbackCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false,
            Reason: available
                ? "Exact locally pinned migration-runner selection is available without execution authority."
                : settings.SelectionEnabled
                    ? "No trusted migration declaration is available."
                    : "Migration-runner selection is disabled.");

        logger.LogInformation(
            "Migration-runner trust selection {State} with {RunnerCount} " +
            "reviewed artifacts and {MigrationCount} exact declarations; runner " +
            "invocation, migration execution, activation, and TX callers remain absent",
            Snapshot.SelectionAvailable ? "ready" : Snapshot.Reason,
            Snapshot.TrustedRunnerCount,
            Snapshot.TrustedMigrationCount);
    }

    public ReleaseMigrationRunnerTrustDiagnostics Snapshot { get; }

    internal bool TrySelect(
        string migrationIdentity,
        int fromConfigurationSchemaVersion,
        int toConfigurationSchemaVersion,
        out ReleaseMigrationTrustedRunner? runner,
        out ReleaseMigrationRunnerMapping? mapping)
    {
        runner = null;
        mapping = null;
        if (!Snapshot.SelectionAvailable)
        {
            return false;
        }

        foreach (ReleaseMigrationTrustedRunner candidate in m_runners)
        {
            ReleaseMigrationRunnerMapping? candidateMapping =
                candidate.Migrations.SingleOrDefault(candidateDeclaration =>
                    string.Equals(
                        candidateDeclaration.MigrationIdentity,
                        migrationIdentity,
                        StringComparison.Ordinal) &&
                    candidateDeclaration.FromConfigurationSchemaVersion ==
                        fromConfigurationSchemaVersion &&
                    candidateDeclaration.ToConfigurationSchemaVersion ==
                        toConfigurationSchemaVersion);
            if (candidateMapping is null)
            {
                continue;
            }
            if (runner is not null)
            {
                runner = null;
                mapping = null;
                return false;
            }
            runner = candidate;
            mapping = candidateMapping;
        }

        return runner is not null && mapping is not null;
    }

    private static ReleaseMigrationTrustedRunner[] LoadRunners(
        ReleaseMigrationRunnerTrustSettings settings)
    {
        ReleaseMigrationRunnerTrustEntrySettings[] configured =
            settings.Runners ?? [];
        if (configured.Length > MaximumTrustedRunners)
        {
            throw new InvalidOperationException(
                $"{ReleaseMigrationRunnerTrustSettings.SectionName}:Runners " +
                $"supports at most {MaximumTrustedRunners} trusted artifacts.");
        }
        if (settings.SelectionEnabled && configured.Length == 0)
        {
            throw new InvalidOperationException(
                $"{ReleaseMigrationRunnerTrustSettings.SectionName}:Runners " +
                "must contain at least one trusted artifact when selection is enabled.");
        }

        HashSet<string> runnerIdentities = new(StringComparer.Ordinal);
        HashSet<string> paths = new(PathComparer);
        HashSet<string> digests = new(StringComparer.Ordinal);
        HashSet<string> migrationIdentities = new(StringComparer.Ordinal);
        List<ReleaseMigrationTrustedRunner> loaded = [];
        int migrationCount = 0;

        foreach (ReleaseMigrationRunnerTrustEntrySettings? configuredRunner in
            configured)
        {
            if (configuredRunner is null)
            {
                throw new InvalidOperationException(
                    $"{ReleaseMigrationRunnerTrustSettings.SectionName}:Runners " +
                    "contains a null entry.");
            }

            string runnerIdentity = ValidateToken(
                configuredRunner.RunnerIdentity,
                MaximumRunnerIdentityLength,
                "Each migration runner requires a canonical RunnerIdentity.");
            if (!runnerIdentities.Add(runnerIdentity))
            {
                throw new InvalidOperationException(
                    "Migration runner identities must be unique.");
            }
            if (configuredRunner.RunnerProtocolVersion !=
                CurrentRunnerProtocolVersion)
            {
                throw new InvalidOperationException(
                    $"Migration runner '{runnerIdentity}' uses an unsupported " +
                    "runner protocol version.");
            }

            string runnerPath = ValidateCanonicalPath(
                configuredRunner.RunnerPath,
                runnerIdentity);
            if (!paths.Add(runnerPath))
            {
                throw new InvalidOperationException(
                    "Each trusted migration runner must use a distinct artifact path.");
            }

            string configuredDigest = ValidateSha256(
                configuredRunner.Sha256,
                runnerIdentity);
            if (!digests.Add(configuredDigest))
            {
                throw new InvalidOperationException(
                    "Each trusted migration runner must use a distinct pinned digest.");
            }

            ReleaseMigrationRunnerTrustMappingSettings[] mappings =
                configuredRunner.Migrations ?? [];
            if (mappings.Length is 0 or > MaximumMigrationsPerRunner)
            {
                throw new InvalidOperationException(
                    $"Migration runner '{runnerIdentity}' must declare from 1 " +
                    $"through {MaximumMigrationsPerRunner} supported migrations.");
            }
            migrationCount = checked(migrationCount + mappings.Length);
            if (migrationCount > MaximumTrustedMigrations)
            {
                throw new InvalidOperationException(
                    $"{ReleaseMigrationRunnerTrustSettings.SectionName} supports " +
                    $"at most {MaximumTrustedMigrations} migration declarations.");
            }

            List<ReleaseMigrationRunnerMapping> trustedMappings = [];
            foreach (ReleaseMigrationRunnerTrustMappingSettings? configuredMapping in
                mappings)
            {
                if (configuredMapping is null)
                {
                    throw new InvalidOperationException(
                        $"Migration runner '{runnerIdentity}' contains a null " +
                        "migration mapping.");
                }
                string migrationIdentity = ValidateToken(
                    configuredMapping.MigrationIdentity,
                    MaximumMigrationIdentityLength,
                    "Each trusted migration mapping requires a canonical signed identity.");
                if (!migrationIdentities.Add(migrationIdentity))
                {
                    throw new InvalidOperationException(
                        "Each signed migration identity may map to only one trusted runner.");
                }
                int fromSchema =
                    configuredMapping.FromConfigurationSchemaVersion;
                int toSchema = configuredMapping.ToConfigurationSchemaVersion;
                if (fromSchema < 1 || toSchema <= fromSchema)
                {
                    throw new InvalidOperationException(
                        $"Migration mapping '{migrationIdentity}' requires an " +
                        "increasing positive configuration-schema transition.");
                }
                trustedMappings.Add(new ReleaseMigrationRunnerMapping(
                    migrationIdentity,
                    fromSchema,
                    toSchema));
            }

            LoadedRunnerArtifact artifact = ReadAndValidateRunner(
                runnerPath,
                configuredDigest,
                runnerIdentity);
            loaded.Add(new ReleaseMigrationTrustedRunner(
                runnerIdentity,
                configuredRunner.RunnerProtocolVersion,
                runnerPath,
                artifact.Length,
                artifact.Sha256,
                artifact.LastWriteTimeUtc,
                trustedMappings));
        }

        return [.. loaded];
    }

    private static LoadedRunnerArtifact ReadAndValidateRunner(
        string path,
        string configuredDigest,
        string runnerIdentity)
    {
        try
        {
            ValidatePathChain(path, runnerIdentity);
            FileInfo before = new(path);
            before.Refresh();
            ValidateRunnerFile(before, runnerIdentity);
            long length = before.Length;
            DateTime lastWrite = before.LastWriteTimeUtc;
            UnixFileMode? mode = OperatingSystem.IsLinux()
                ? File.GetUnixFileMode(path)
                : null;

            byte[] digest;
            using (FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan))
            {
                if (stream.Length != length)
                {
                    throw Changed(runnerIdentity);
                }
                digest = SHA256.HashData(stream);
            }

            ValidatePathChain(path, runnerIdentity);
            FileInfo after = new(path);
            after.Refresh();
            ValidateRunnerFile(after, runnerIdentity);
            UnixFileMode? afterMode = OperatingSystem.IsLinux()
                ? File.GetUnixFileMode(path)
                : null;
            if (after.Length != length ||
                after.LastWriteTimeUtc != lastWrite ||
                afterMode != mode)
            {
                throw Changed(runnerIdentity);
            }
            if (!string.Equals(
                    Convert.ToHexString(digest).ToLowerInvariant(),
                    configuredDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Migration runner '{runnerIdentity}' does not match its " +
                    "pinned SHA-256 digest.");
            }

            return new LoadedRunnerArtifact(
                length,
                digest,
                lastWrite);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException or
                PathTooLongException or CryptographicException)
        {
            throw new InvalidOperationException(
                $"Migration runner '{runnerIdentity}' could not be validated as " +
                "one immutable locally pinned artifact.",
                exception);
        }
    }

    private static void ValidateRunnerFile(
        FileInfo file,
        string runnerIdentity)
    {
        if (!file.Exists ||
            (file.Attributes & FileAttributes.Directory) != 0 ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.LinkTarget is not null ||
            file.Length is < 1 or > MaximumRunnerFileBytes)
        {
            throw new InvalidOperationException(
                $"Migration runner '{runnerIdentity}' is missing, linked, " +
                "empty, oversized, or not a regular file.");
        }

        if (OperatingSystem.IsLinux())
        {
            UnixFileMode mode = File.GetUnixFileMode(file.FullName);
            if ((mode & UnixFileMode.UserRead) == 0 ||
                (mode & UnixFileMode.UserExecute) == 0 ||
                (mode & AnyWritableUnixModes) != 0)
            {
                throw new InvalidOperationException(
                    $"Migration runner '{runnerIdentity}' must be owner-readable, " +
                    "owner-executable, and immutable.");
            }
        }
    }

    private static void ValidatePathChain(
        string filePath,
        string runnerIdentity)
    {
        DirectoryInfo? directory = new FileInfo(filePath).Directory;
        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Migration runner '{runnerIdentity}' has no containing directory.");
        }

        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"Migration runner '{runnerIdentity}' has a missing or linked " +
                "containing directory.");
        }
        if (OperatingSystem.IsLinux())
        {
            UnixFileMode mode = File.GetUnixFileMode(directory.FullName);
            if ((mode & ForbiddenSharedWritableUnixModes) != 0)
            {
                throw new InvalidOperationException(
                    $"Migration runner '{runnerIdentity}' has a shared-writable " +
                    "containing directory.");
            }
        }
    }

    private static string ValidateCanonicalPath(
        string? value,
        string runnerIdentity)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length is 0 or > MaximumRunnerPathLength ||
            !string.Equals(value, path, StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                $"Migration runner '{runnerIdentity}' requires an absolute " +
                "canonical RunnerPath.");
        }
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(path, fullPath, PathComparison))
        {
            throw new InvalidOperationException(
                $"Migration runner '{runnerIdentity}' RunnerPath must already be " +
                "canonical.");
        }
        return fullPath;
    }

    private static string ValidateToken(
        string? value,
        int maximumLength,
        string message)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '_' and not '-')
            {
                throw new InvalidOperationException(message);
            }
        }
        return value;
    }

    private static string ValidateSha256(
        string? value,
        string runnerIdentity)
    {
        if (value is null || value.Length != 64)
        {
            throw new InvalidOperationException(
                $"Migration runner '{runnerIdentity}' requires one canonical " +
                "lowercase SHA-256 digest.");
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character) &&
                character is not (>= 'a' and <= 'f'))
            {
                throw new InvalidOperationException(
                    $"Migration runner '{runnerIdentity}' requires one canonical " +
                    "lowercase SHA-256 digest.");
            }
        }
        return value;
    }

    private static InvalidOperationException Changed(string runnerIdentity) =>
        new($"Migration runner '{runnerIdentity}' changed while it was validated.");

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record LoadedRunnerArtifact(
        long Length,
        byte[] Sha256,
        DateTime LastWriteTimeUtc);
}

public enum VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
{
    None = 0,
    MigrationPlanNotEligible = 1,
    MigrationPlanUnavailable = 2,
    MigrationPlanMismatch = 3,
    RunnerTrustDisabled = 4,
    TrustedRunnerNotFound = 5,
    TrustedRunnerInvalid = 6
}

public sealed record VerifiedReleaseActivationMigrationRunnerSelectionReport(
    bool Succeeded,
    VerifiedReleaseActivationMigrationRunnerSelectionFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    ReleaseMigrationKind? MigrationKind,
    int? FromConfigurationSchemaVersion,
    int? ToConfigurationSchemaVersion,
    bool MigrationRequired,
    bool NoOpMigrationResolved,
    bool ExactMigrationPlanBound,
    bool RunnerTrustEnabled,
    bool MigrationRunnerRequired,
    bool MigrationRunnerSelected,
    bool RunnerArtifactValidatedAtStartup,
    int? RunnerProtocolVersion,
    bool MigrationSourceReadPerformed,
    bool RunnerInvoked,
    bool MigrationExecutionPerformed,
    bool MigrationReady,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationMigrationRunnerSelection? Selection
    {
        get;
        init;
    }

    internal static VerifiedReleaseActivationMigrationRunnerSelectionReport Failure(
        VerifiedReleaseActivationMigrationRunnerSelectionFailureCode failureCode,
        string message,
        VerifiedReleaseActivationMigrationPlanReport? planReport = null,
        bool runnerTrustEnabled = false) =>
        new(
            false,
            failureCode,
            message,
            planReport?.SetupRevision,
            planReport?.InstalledReleaseIdentity ?? string.Empty,
            planReport?.TargetReleaseIdentity ?? string.Empty,
            planReport?.MigrationKind,
            planReport?.FromConfigurationSchemaVersion,
            planReport?.ToConfigurationSchemaVersion,
            planReport?.MigrationRequired ?? false,
            NoOpMigrationResolved: false,
            ExactMigrationPlanBound: false,
            runnerTrustEnabled,
            MigrationRunnerRequired: planReport?.MigrationRequired ?? false,
            MigrationRunnerSelected: false,
            RunnerArtifactValidatedAtStartup: false,
            RunnerProtocolVersion: null,
            MigrationSourceReadPerformed: false,
            RunnerInvoked: false,
            MigrationExecutionPerformed: false,
            MigrationReady: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationMigrationRunnerSelectionReport Success(
        VerifiedReleaseActivationMigrationRunnerSelection selection,
        bool runnerTrustEnabled) =>
        new(
            true,
            VerifiedReleaseActivationMigrationRunnerSelectionFailureCode.None,
            selection.Plan.MigrationRequired
                ? "One locally pinned runner was selected for the exact signed migration declaration without invocation or migration execution."
                : "The exact signed no-migration declaration requires no runner selection or execution.",
            selection.Plan.ActivationPlan.SetupRevision,
            selection.Plan.ActivationPlan.InstalledReleaseIdentity,
            selection.Plan.ActivationPlan.TargetReleaseIdentity,
            selection.Plan.MigrationKind,
            selection.Plan.FromConfigurationSchemaVersion,
            selection.Plan.ToConfigurationSchemaVersion,
            selection.Plan.MigrationRequired,
            NoOpMigrationResolved: !selection.Plan.MigrationRequired,
            ExactMigrationPlanBound: true,
            runnerTrustEnabled,
            MigrationRunnerRequired: selection.Plan.MigrationRequired,
            MigrationRunnerSelected: selection.Runner is not null,
            RunnerArtifactValidatedAtStartup: selection.Runner is not null,
            RunnerProtocolVersion: selection.Runner?.RunnerProtocolVersion,
            MigrationSourceReadPerformed: false,
            RunnerInvoked: false,
            MigrationExecutionPerformed: false,
            MigrationReady: !selection.Plan.MigrationRequired,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            Selection = selection
        };
}

public sealed record VerifiedReleaseActivationMigrationRunnerSelectionDiagnostics(
    bool Registered,
    bool MigrationPlanInputRegistered,
    bool RunnerTrustInputRegistered,
    bool ExactMigrationPlanBindingRegistered,
    bool NoOpMigrationResolutionRegistered,
    bool RequiredRunnerSelectionRegistered,
    bool ExactMigrationIdentityBindingRegistered,
    bool SchemaTransitionBindingRegistered,
    bool RunnerProtocolBindingRegistered,
    bool RunnerArtifactDigestBindingRegistered,
    bool RunnerInvocationRegistered,
    bool MigrationSourceReadRegistered,
    bool FileWriteRegistered,
    bool DirectoryMutationRegistered,
    bool MigrationExecutionRegistered,
    bool MigrationEvidenceRegistered,
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
    bool HealthProbeCallerRegistered,
    bool RollbackCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed class VerifiedReleaseActivationMigrationRunnerSelection
{
    internal VerifiedReleaseActivationMigrationRunnerSelection(
        VerifiedReleaseActivationMigrationPlan plan,
        ReleaseMigrationTrustedRunner? runner,
        ReleaseMigrationRunnerMapping? mapping)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Runner = runner;
        Mapping = mapping;
    }

    internal VerifiedReleaseActivationMigrationPlan Plan { get; }
    internal ReleaseMigrationTrustedRunner? Runner { get; }
    internal ReleaseMigrationRunnerMapping? Mapping { get; }
}

/// <summary>
/// Pure exact-plan selection of one locally pinned runner for one signed required
/// migration declaration. Signed no-migration plans resolve without trust. The
/// selector reads no backup content, reopens no runner file, invokes no process,
/// writes nothing, produces no migration evidence, changes no current pointer,
/// authorizes no activation, and has no operational, service, radio, watchdog,
/// command, lease, or TX caller.
/// </summary>
public sealed class VerifiedReleaseActivationMigrationRunnerSelector
{
    private readonly ReleaseMigrationRunnerTrustRegistry m_trustRegistry;

    public VerifiedReleaseActivationMigrationRunnerSelector(
        ReleaseMigrationRunnerTrustRegistry trustRegistry)
    {
        m_trustRegistry = trustRegistry ??
            throw new ArgumentNullException(nameof(trustRegistry));
        Snapshot = new VerifiedReleaseActivationMigrationRunnerSelectionDiagnostics(
            Registered: true,
            MigrationPlanInputRegistered: true,
            RunnerTrustInputRegistered: true,
            ExactMigrationPlanBindingRegistered: true,
            NoOpMigrationResolutionRegistered: true,
            RequiredRunnerSelectionRegistered: true,
            ExactMigrationIdentityBindingRegistered: true,
            SchemaTransitionBindingRegistered: true,
            RunnerProtocolBindingRegistered: true,
            RunnerArtifactDigestBindingRegistered: true,
            RunnerInvocationRegistered: false,
            MigrationSourceReadRegistered: false,
            FileWriteRegistered: false,
            DirectoryMutationRegistered: false,
            MigrationExecutionRegistered: false,
            MigrationEvidenceRegistered: false,
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
            HealthProbeCallerRegistered: false,
            RollbackCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationMigrationRunnerSelectionDiagnostics Snapshot
    {
        get;
    }

    internal VerifiedReleaseActivationMigrationRunnerSelectionReport Select(
        VerifiedReleaseActivationMigrationPlanReport planReport)
    {
        ArgumentNullException.ThrowIfNull(planReport);
        bool trustEnabled = m_trustRegistry.Snapshot.SelectionEnabled;
        if (!IsEligiblePlanReport(planReport))
        {
            return VerifiedReleaseActivationMigrationRunnerSelectionReport.Failure(
                VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                    .MigrationPlanNotEligible,
                "A successful exact-plan migration plan is required.",
                planReport,
                trustEnabled);
        }

        VerifiedReleaseActivationMigrationPlan? plan = planReport.Plan;
        if (plan is null)
        {
            return VerifiedReleaseActivationMigrationRunnerSelectionReport.Failure(
                VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                    .MigrationPlanUnavailable,
                "The successful migration plan does not retain its internal exact plan.",
                planReport,
                trustEnabled);
        }
        if (!MatchesPlanReport(planReport, plan))
        {
            return VerifiedReleaseActivationMigrationRunnerSelectionReport.Failure(
                VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                    .MigrationPlanMismatch,
                "Migration-plan metadata does not match its exact internal plan.",
                planReport,
                trustEnabled);
        }

        if (!plan.MigrationRequired)
        {
            return VerifiedReleaseActivationMigrationRunnerSelectionReport.Success(
                new VerifiedReleaseActivationMigrationRunnerSelection(
                    plan,
                    runner: null,
                    mapping: null),
                trustEnabled);
        }
        if (!m_trustRegistry.Snapshot.SelectionAvailable)
        {
            return VerifiedReleaseActivationMigrationRunnerSelectionReport.Failure(
                trustEnabled
                    ? VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                        .TrustedRunnerNotFound
                    : VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                        .RunnerTrustDisabled,
                trustEnabled
                    ? "No trusted runner is available for the exact signed migration declaration."
                    : "Migration-runner trust selection is disabled.",
                planReport,
                trustEnabled);
        }

        int fromSchema = plan.FromConfigurationSchemaVersion!.Value;
        int toSchema = plan.ToConfigurationSchemaVersion!.Value;
        if (!m_trustRegistry.TrySelect(
                plan.MigrationIdentity,
                fromSchema,
                toSchema,
                out ReleaseMigrationTrustedRunner? runner,
                out ReleaseMigrationRunnerMapping? mapping) ||
            runner is null ||
            mapping is null)
        {
            return VerifiedReleaseActivationMigrationRunnerSelectionReport.Failure(
                VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                    .TrustedRunnerNotFound,
                "No trusted runner is available for the exact signed migration declaration.",
                planReport,
                trustEnabled);
        }
        if (!ValidateSelection(plan, runner, mapping))
        {
            return VerifiedReleaseActivationMigrationRunnerSelectionReport.Failure(
                VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                    .TrustedRunnerInvalid,
                "The selected migration runner does not retain a valid exact trust binding.",
                planReport,
                trustEnabled);
        }

        return VerifiedReleaseActivationMigrationRunnerSelectionReport.Success(
            new VerifiedReleaseActivationMigrationRunnerSelection(
                plan,
                runner,
                mapping),
            trustEnabled);
    }

    private static bool IsEligiblePlanReport(
        VerifiedReleaseActivationMigrationPlanReport report) =>
        report.Succeeded &&
        report.FailureCode == VerifiedReleaseActivationMigrationPlanFailureCode.None &&
        report.SetupRevision is > 0 &&
        !string.IsNullOrEmpty(report.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(report.TargetReleaseIdentity) &&
        report.MigrationKind is ReleaseMigrationKind.None or
            ReleaseMigrationKind.Required &&
        report.ExactActivationPlanBound &&
        report.ExactConfigurationBackupBound &&
        report.SourceBackupImmutable &&
        !report.MigrationRunnerSelected &&
        !report.SourceReadPerformed &&
        !report.FileWritePerformed &&
        !report.MigrationExecutionPerformed &&
        !report.CurrentPointerChanged &&
        !report.ActivationAuthorized;

    private static bool MatchesPlanReport(
        VerifiedReleaseActivationMigrationPlanReport report,
        VerifiedReleaseActivationMigrationPlan plan)
    {
        if (report.SetupRevision != plan.ActivationPlan.SetupRevision ||
            !string.Equals(
                report.InstalledReleaseIdentity,
                plan.ActivationPlan.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.TargetReleaseIdentity,
                plan.ActivationPlan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            report.MigrationKind != plan.MigrationKind ||
            report.FromConfigurationSchemaVersion !=
                plan.FromConfigurationSchemaVersion ||
            report.ToConfigurationSchemaVersion !=
                plan.ToConfigurationSchemaVersion ||
            report.MigrationRequired != plan.MigrationRequired ||
            report.NoOpMigrationResolved == plan.MigrationRequired ||
            report.StagedCopyRequired != plan.MigrationRequired ||
            report.MigrationManifestRequired != plan.MigrationRequired ||
            report.AtomicPublicationRequired != plan.MigrationRequired ||
            report.MigrationRunnerRequired != plan.MigrationRequired ||
            report.MigrationReady != !plan.MigrationRequired ||
            plan.ConfigurationBackup.ManifestSha256.Length != 32 ||
            !ReferenceEquals(
                plan.ConfigurationBackup.Plan.ActivationPlan,
                plan.ActivationPlan))
        {
            return false;
        }

        if (!plan.MigrationRequired)
        {
            return string.IsNullOrEmpty(plan.MigrationIdentity) &&
                plan.FromConfigurationSchemaVersion is null &&
                plan.ToConfigurationSchemaVersion is null &&
                plan.Sources.Count == 0 &&
                string.IsNullOrEmpty(plan.MigrationRootPath) &&
                string.IsNullOrEmpty(plan.StagingPath) &&
                string.IsNullOrEmpty(plan.PublishedPath) &&
                string.IsNullOrEmpty(plan.ManifestPath);
        }

        return IsBoundedAsciiToken(
                plan.MigrationIdentity,
                ReleaseMigrationRunnerTrustRegistry.MaximumMigrationIdentityLength) &&
            plan.FromConfigurationSchemaVersion is >= 1 &&
            plan.ToConfigurationSchemaVersion >
                plan.FromConfigurationSchemaVersion &&
            plan.Sources.Count == 3 &&
            !string.IsNullOrEmpty(plan.MigrationRootPath) &&
            !string.IsNullOrEmpty(plan.StagingPath) &&
            !string.IsNullOrEmpty(plan.PublishedPath) &&
            !string.IsNullOrEmpty(plan.ManifestPath) &&
            !plan.ExistingMigrationOverwriteAllowed &&
            plan.AtomicPublicationRequired &&
            plan.MigrationRunnerRequired;
    }

    private static bool ValidateSelection(
        VerifiedReleaseActivationMigrationPlan plan,
        ReleaseMigrationTrustedRunner runner,
        ReleaseMigrationRunnerMapping mapping) =>
        runner.RunnerProtocolVersion ==
            ReleaseMigrationRunnerTrustRegistry.CurrentRunnerProtocolVersion &&
        runner.RunnerLength is > 0 and <=
            ReleaseMigrationRunnerTrustRegistry.MaximumRunnerFileBytes &&
        runner.Sha256.Count == 32 &&
        runner.LastWriteTimeUtc != default &&
        Path.IsPathFullyQualified(runner.RunnerPath) &&
        string.Equals(
            Path.GetFullPath(runner.RunnerPath),
            runner.RunnerPath,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal) &&
        string.Equals(
            mapping.MigrationIdentity,
            plan.MigrationIdentity,
            StringComparison.Ordinal) &&
        mapping.FromConfigurationSchemaVersion ==
            plan.FromConfigurationSchemaVersion &&
        mapping.ToConfigurationSchemaVersion ==
            plan.ToConfigurationSchemaVersion;

    private static bool IsBoundedAsciiToken(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }
        return true;
    }
}
