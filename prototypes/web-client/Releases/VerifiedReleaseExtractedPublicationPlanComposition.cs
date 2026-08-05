using System.Collections.ObjectModel;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseExtractedPublicationPlanFailureCode
{
    None = 0,
    ExtractionNotEligible = 1,
    ExtractedReleaseUnavailable = 2,
    ExtractionSummaryMismatch = 3,
    InvalidSourcePlan = 4,
    InvalidPublicationPaths = 5,
    InvalidExtractedFileInventory = 6
}

public sealed record VerifiedReleaseExtractedPublicationPlanCompositionResult(
    bool Succeeded,
    VerifiedReleaseExtractedPublicationPlanFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    int PackageCount,
    int FileCount,
    int DirectoryCount,
    long PublicationBytes,
    bool ManifestIncluded,
    bool ImmutableSourceRequired,
    bool AtomicDirectoryPublishRequired,
    bool CurrentPointerChanged,
    bool ActivationPerformed)
{
    internal VerifiedReleaseExtractedPublicationPlan? Plan { get; init; }

    internal static VerifiedReleaseExtractedPublicationPlanCompositionResult Failure(
        VerifiedReleaseExtractedPublicationPlanFailureCode failureCode,
        string message,
        VerifiedReleaseArchiveExtractionReport? extraction = null) =>
        new(
            false,
            failureCode,
            message,
            extraction?.SetupRevision,
            extraction?.InstalledReleaseIdentity ?? string.Empty,
            extraction?.TargetReleaseIdentity ?? string.Empty,
            extraction?.PackageCount ?? 0,
            extraction?.ExtractedFileCount ?? 0,
            extraction?.ExtractedDirectoryCount ?? 0,
            extraction?.ExpandedBytes ?? 0,
            extraction?.ManifestCopied ?? false,
            ImmutableSourceRequired: true,
            AtomicDirectoryPublishRequired: true,
            CurrentPointerChanged: false,
            ActivationPerformed: false);

    internal static VerifiedReleaseExtractedPublicationPlanCompositionResult Success(
        VerifiedReleaseExtractedPublicationPlan plan) =>
        new(
            true,
            VerifiedReleaseExtractedPublicationPlanFailureCode.None,
            "A verified extracted release publication plan was composed without publishing or activation.",
            plan.Source.Plan.SetupRevision,
            plan.Source.Plan.InstalledReleaseIdentity,
            plan.Source.Plan.TargetReleaseIdentity,
            plan.Source.Plan.Packages.Count,
            plan.Files.Count,
            plan.DirectoryCount,
            plan.PublicationBytes,
            ManifestIncluded: true,
            ImmutableSourceRequired: true,
            AtomicDirectoryPublishRequired: true,
            CurrentPointerChanged: false,
            ActivationPerformed: false)
        {
            Plan = plan
        };
}

public sealed record VerifiedReleaseExtractedPublicationPlanDiagnostics(
    bool Registered,
    bool VerifiedExtractionInputRegistered,
    bool ExtractionSummaryValidationRegistered,
    bool ImmutableFileInventoryCompositionRegistered,
    bool ExecutableIntentCompositionRegistered,
    bool SourcePathCompositionRegistered,
    bool TargetPathCompositionRegistered,
    bool NetworkDownloadRegistered,
    bool ArchiveExtractionExecutionRegistered,
    bool FileWriteRegistered,
    bool AtomicDirectoryPublishExecutionRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationRegistered,
    bool RollbackRegistered,
    bool MigrationExecutionRegistered,
    bool ServiceControlRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed class VerifiedReleaseExtractedPublicationFilePlan
{
    private readonly byte[] m_sha256;

    internal VerifiedReleaseExtractedPublicationFilePlan(
        ReleasePackageRole role,
        string relativePath,
        string sourcePath,
        string targetPath,
        long length,
        ReadOnlySpan<byte> sha256,
        bool executable)
    {
        if (string.IsNullOrEmpty(relativePath) ||
            string.IsNullOrEmpty(sourcePath) ||
            string.IsNullOrEmpty(targetPath) ||
            length < 0 ||
            sha256.Length != 32)
        {
            throw new ArgumentException(
                "An extracted publication file plan requires canonical paths, a bounded length, and SHA-256 metadata.");
        }

        Role = role;
        RelativePath = relativePath;
        SourcePath = sourcePath;
        TargetPath = targetPath;
        Length = length;
        m_sha256 = sha256.ToArray();
        Executable = executable;
    }

    internal ReleasePackageRole Role { get; }
    internal string RelativePath { get; }
    internal string SourcePath { get; }
    internal string TargetPath { get; }
    internal long Length { get; }
    internal ReadOnlySpan<byte> Sha256 => m_sha256;
    internal bool Executable { get; }
}

internal sealed class VerifiedReleaseExtractedPublicationPlan
{
    private readonly ReadOnlyCollection<
        VerifiedReleaseExtractedPublicationFilePlan> m_files;

    internal VerifiedReleaseExtractedPublicationPlan(
        VerifiedExtractedRelease source,
        string sourcePath,
        string targetPath,
        IReadOnlyList<VerifiedReleaseExtractedPublicationFilePlan> files,
        int directoryCount,
        long publicationBytes)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        SourcePath = sourcePath ?? string.Empty;
        TargetPath = targetPath ?? string.Empty;
        m_files = Array.AsReadOnly(files.ToArray());
        DirectoryCount = directoryCount;
        PublicationBytes = publicationBytes;
    }

    internal VerifiedExtractedRelease Source { get; }
    internal string SourcePath { get; }
    internal string TargetPath { get; }
    internal IReadOnlyList<VerifiedReleaseExtractedPublicationFilePlan> Files =>
        m_files;
    internal int DirectoryCount { get; }
    internal long PublicationBytes { get; }
}

/// <summary>
/// Pure composition of one successful immutable extraction result into an
/// exact future inactive-release publication plan. It validates the retained
/// source token, role-root file inventory, digests, executable intent, and
/// canonical source/target mappings. It performs no filesystem I/O, archive
/// extraction, write, rename, publication, current-pointer mutation,
/// activation, rollback, migration, service control, Admin/browser action,
/// radio/watchdog command, lease mutation, keying, or transmit operation.
/// </summary>
public sealed class VerifiedReleaseExtractedPublicationPlanComposer
{
    private static readonly ReleasePackageRole[] RequiredRoles =
    [
        ReleasePackageRole.GatewayWeb,
        ReleasePackageRole.Broker,
        ReleasePackageRole.AetherRemoteAgent,
        ReleasePackageRole.StationEngine
    ];

    public VerifiedReleaseExtractedPublicationPlanComposer()
    {
        Snapshot = new VerifiedReleaseExtractedPublicationPlanDiagnostics(
            Registered: true,
            VerifiedExtractionInputRegistered: true,
            ExtractionSummaryValidationRegistered: true,
            ImmutableFileInventoryCompositionRegistered: true,
            ExecutableIntentCompositionRegistered: true,
            SourcePathCompositionRegistered: true,
            TargetPathCompositionRegistered: true,
            NetworkDownloadRegistered: false,
            ArchiveExtractionExecutionRegistered: false,
            FileWriteRegistered: false,
            AtomicDirectoryPublishExecutionRegistered: false,
            CurrentPointerMutationRegistered: false,
            ActivationRegistered: false,
            RollbackRegistered: false,
            MigrationExecutionRegistered: false,
            ServiceControlRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseExtractedPublicationPlanDiagnostics Snapshot { get; }

    public VerifiedReleaseExtractedPublicationPlanCompositionResult Compose(
        VerifiedReleaseArchiveExtractionReport extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        if (!IsEligibleExtraction(extraction))
        {
            return VerifiedReleaseExtractedPublicationPlanCompositionResult.Failure(
                VerifiedReleaseExtractedPublicationPlanFailureCode.ExtractionNotEligible,
                "A successful immutable verified extraction without publication or cleanup ambiguity is required.",
                extraction);
        }

        VerifiedExtractedRelease? source = extraction.ExtractedRelease;
        if (source is null)
        {
            return VerifiedReleaseExtractedPublicationPlanCompositionResult.Failure(
                VerifiedReleaseExtractedPublicationPlanFailureCode.ExtractedReleaseUnavailable,
                "The successful extraction does not retain its verified internal release token.",
                extraction);
        }

        if (!MatchesExtractionSummary(extraction, source))
        {
            return VerifiedReleaseExtractedPublicationPlanCompositionResult.Failure(
                VerifiedReleaseExtractedPublicationPlanFailureCode.ExtractionSummaryMismatch,
                "The extraction summary does not match its retained verified release token.",
                extraction);
        }

        VerifiedReleaseInstallationPlan sourcePlan = source.Plan;
        if (!ValidateSourcePlan(sourcePlan, source))
        {
            return VerifiedReleaseExtractedPublicationPlanCompositionResult.Failure(
                VerifiedReleaseExtractedPublicationPlanFailureCode.InvalidSourcePlan,
                "The verified extracted release source plan is incomplete or non-canonical.",
                extraction);
        }

        string sourcePath;
        string targetPath;
        try
        {
            sourcePath = CanonicalExtractionPath(sourcePlan, source.ExtractionPath);
            targetPath = CanonicalTargetPath(sourcePlan);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return VerifiedReleaseExtractedPublicationPlanCompositionResult.Failure(
                VerifiedReleaseExtractedPublicationPlanFailureCode.InvalidPublicationPaths,
                "The verified extracted release cannot produce canonical inactive-publication paths.",
                extraction);
        }

        if (!TryCreateFilePlans(
                source,
                sourcePath,
                targetPath,
                out VerifiedReleaseExtractedPublicationFilePlan[] filePlans,
                out int directoryCount,
                out long publicationBytes))
        {
            return VerifiedReleaseExtractedPublicationPlanCompositionResult.Failure(
                VerifiedReleaseExtractedPublicationPlanFailureCode.InvalidExtractedFileInventory,
                "The verified extracted file inventory cannot produce one exact inactive-publication plan.",
                extraction);
        }

        VerifiedReleaseExtractedPublicationPlan plan = new(
            source,
            sourcePath,
            targetPath,
            filePlans,
            directoryCount,
            publicationBytes);
        return VerifiedReleaseExtractedPublicationPlanCompositionResult.Success(plan);
    }

    private static bool IsEligibleExtraction(
        VerifiedReleaseArchiveExtractionReport extraction) =>
        extraction.Succeeded &&
        extraction.FailureCode ==
            VerifiedReleaseArchiveExtractionFailureCode.None &&
        extraction.SetupRevision is >= 1 &&
        !string.IsNullOrEmpty(extraction.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(extraction.TargetReleaseIdentity) &&
        extraction.PackageCount == RequiredRoles.Length &&
        extraction.ExtractedFileCount >= RequiredRoles.Length + 1 &&
        extraction.ExtractedFileCount <=
            VerifiedReleaseArchiveExtractionService.MaximumExtractedFileCount &&
        extraction.ExtractedDirectoryCount >= RequiredRoles.Length &&
        extraction.ExtractedDirectoryCount <=
            VerifiedReleaseArchiveExtractionService.MaximumExtractedDirectoryCount &&
        extraction.ExpandedBytes > 0 &&
        extraction.ExpandedBytes <=
            VerifiedReleaseArchiveExtractionService.MaximumExpandedBytes &&
        extraction.SourceArchivesVerified &&
        extraction.ManifestCopied &&
        extraction.ImmutableExtractionTree &&
        !extraction.TargetPublished &&
        !extraction.CurrentPointerChanged &&
        !extraction.CleanupRequired;

    private static bool MatchesExtractionSummary(
        VerifiedReleaseArchiveExtractionReport extraction,
        VerifiedExtractedRelease source)
    {
        VerifiedReleaseInstallationPlan plan = source.Plan;
        long calculatedBytes;
        try
        {
            calculatedBytes = source.Files.Sum(file => file.Length);
        }
        catch (OverflowException)
        {
            return false;
        }

        return plan.SetupRevision == extraction.SetupRevision &&
            string.Equals(
                plan.InstalledReleaseIdentity,
                extraction.InstalledReleaseIdentity,
                StringComparison.Ordinal) &&
            string.Equals(
                plan.TargetReleaseIdentity,
                extraction.TargetReleaseIdentity,
                StringComparison.Ordinal) &&
            plan.Packages.Count == extraction.PackageCount &&
            source.Files.Count == extraction.ExtractedFileCount &&
            source.DirectoryCount == extraction.ExtractedDirectoryCount &&
            source.ExpandedBytes == extraction.ExpandedBytes &&
            calculatedBytes == source.ExpandedBytes;
    }

    private static bool ValidateSourcePlan(
        VerifiedReleaseInstallationPlan plan,
        VerifiedExtractedRelease source)
    {
        if (plan.SetupRevision < 1 ||
            !IsCanonicalReleaseIdentity(plan.InstalledReleaseIdentity) ||
            !IsCanonicalReleaseIdentity(plan.TargetReleaseIdentity) ||
            string.Equals(
                plan.InstalledReleaseIdentity,
                plan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            !ReleaseSemanticVersion.TryParse(
                plan.TargetVersion,
                out ReleaseSemanticVersion parsedVersion) ||
            !string.Equals(
                plan.TargetVersion,
                CanonicalSemanticVersion(parsedVersion),
                StringComparison.Ordinal) ||
            plan.Architecture is not ReleaseManifestArchitecture.LinuxX64 and
                not ReleaseManifestArchitecture.LinuxArm64 ||
            plan.UpdateChannel is not InstallationUpdateChannel.Stable and
                not InstallationUpdateChannel.Beta and
                not InstallationUpdateChannel.Pinned ||
            plan.ManifestLength is < 1 or >
                SignedReleaseManifestJson.MaximumManifestBytes ||
            plan.ManifestSha256.Length != 32 ||
            !IsCanonicalAbsolutePath(plan.BundleDirectory) ||
            !IsCanonicalAbsolutePath(plan.ReleaseRootPath) ||
            !IsCanonicalAbsolutePath(plan.DeploymentRootPath) ||
            !IsCanonicalAbsolutePath(plan.TargetReleasePath) ||
            !IsCanonicalAbsolutePath(source.SourceStagedRelease.StagingPath) ||
            !IsCanonicalAbsolutePath(source.ExtractionPath) ||
            !PathEquals(
                Path.GetDirectoryName(plan.ReleaseRootPath),
                plan.DeploymentRootPath) ||
            !PathEquals(
                Path.GetDirectoryName(plan.TargetReleasePath),
                plan.ReleaseRootPath) ||
            !string.Equals(
                Path.GetFileName(plan.TargetReleasePath),
                plan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            plan.Packages.Count != RequiredRoles.Length ||
            plan.InstallTransmitSupport != plan.TxSupportCapable)
        {
            return false;
        }

        if (plan.UpdateChannel == InstallationUpdateChannel.Pinned)
        {
            if (!string.Equals(
                    plan.PinnedReleaseIdentity,
                    plan.TargetReleaseIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (!string.IsNullOrEmpty(plan.PinnedReleaseIdentity))
        {
            return false;
        }

        HashSet<ReleasePackageRole> roles = [];
        HashSet<string> identities = new(StringComparer.Ordinal);
        HashSet<string> sourcePaths = new(StringComparer.Ordinal);
        foreach (VerifiedReleaseInstallationPackagePlan package in plan.Packages)
        {
            if (!RequiredRoles.Contains(package.Role) ||
                !roles.Add(package.Role) ||
                string.IsNullOrWhiteSpace(package.PackageIdentity) ||
                !string.Equals(
                    package.PackageIdentity,
                    package.PackageIdentity.Trim(),
                    StringComparison.Ordinal) ||
                !identities.Add(package.PackageIdentity) ||
                !ReleasePackagePath.IsSafe(package.SourceRelativePath) ||
                !sourcePaths.Add(package.SourceRelativePath) ||
                package.Length is < 1 or >
                    SignedReleaseManifestVerifier.MaximumDeclaredPackageLength ||
                package.Sha256.Length != 32 ||
                !IsCanonicalAbsolutePath(package.TargetPath))
            {
                return false;
            }

            string expectedTarget;
            try
            {
                expectedTarget = Path.GetFullPath(
                    Path.Combine(
                        plan.TargetReleasePath,
                        package.SourceRelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or
                    PathTooLongException)
            {
                return false;
            }

            if (!PathEquals(expectedTarget, package.TargetPath) ||
                !IsStrictDescendant(plan.TargetReleasePath, expectedTarget))
            {
                return false;
            }
        }

        return roles.SetEquals(RequiredRoles);
    }

    private static string CanonicalExtractionPath(
        VerifiedReleaseInstallationPlan plan,
        string extractionPath)
    {
        string root = Path.GetFullPath(
            Path.Combine(
                plan.DeploymentRootPath,
                VerifiedReleaseArchiveExtractionService
                    .ExtractionStagingDirectoryName));
        string source = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(extractionPath));
        if (!PathEquals(Path.GetDirectoryName(root), plan.DeploymentRootPath) ||
            !PathEquals(Path.GetDirectoryName(source), root))
        {
            throw new InvalidOperationException(
                "The extraction source is not a direct private transaction child.");
        }

        string name = Path.GetFileName(source);
        string prefix = plan.TargetReleaseIdentity + ".";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            name.Length != prefix.Length + 32 ||
            !name.AsSpan(prefix.Length).ToString().All(IsLowerHex))
        {
            throw new InvalidOperationException(
                "The extraction transaction identity is non-canonical.");
        }
        return source;
    }

    private static string CanonicalTargetPath(
        VerifiedReleaseInstallationPlan plan)
    {
        string target = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                Path.Combine(
                    plan.ReleaseRootPath,
                    plan.TargetReleaseIdentity)));
        if (!PathEquals(target, plan.TargetReleasePath) ||
            !PathEquals(Path.GetDirectoryName(target), plan.ReleaseRootPath))
        {
            throw new InvalidOperationException(
                "The inactive release target is non-canonical.");
        }
        return target;
    }

    private static bool TryCreateFilePlans(
        VerifiedExtractedRelease source,
        string sourcePath,
        string targetPath,
        out VerifiedReleaseExtractedPublicationFilePlan[] filePlans,
        out int directoryCount,
        out long publicationBytes)
    {
        filePlans = [];
        directoryCount = 0;
        publicationBytes = 0;

        if (source.Files.Count is < 5 or >
                VerifiedReleaseArchiveExtractionService.MaximumExtractedFileCount ||
            source.DirectoryCount is < 4 or >
                VerifiedReleaseArchiveExtractionService.MaximumExtractedDirectoryCount)
        {
            return false;
        }

        HashSet<string> relativePaths = new(StringComparer.Ordinal);
        HashSet<string> sourcePaths = new(StringComparer.Ordinal);
        HashSet<string> targetPaths = new(StringComparer.Ordinal);
        HashSet<string> directories = new(StringComparer.Ordinal);
        HashSet<ReleasePackageRole> rolesWithFiles = [];
        List<VerifiedReleaseExtractedPublicationFilePlan> plans = [];
        bool manifestFound = false;
        long total = 0;

        foreach (VerifiedExtractedReleaseFile file in source.Files)
        {
            if (!ReleasePackagePath.IsSafe(file.RelativePath) ||
                !relativePaths.Add(file.RelativePath) ||
                file.Length < 0 ||
                file.Length >
                    VerifiedReleaseArchiveExtractionService
                        .MaximumExtractedFileLength ||
                file.Sha256.Length != 32)
            {
                return false;
            }

            bool isManifest = string.Equals(
                file.RelativePath,
                LocalOfflineReleaseBundleVerificationService.ManifestFileName,
                StringComparison.Ordinal);
            if (isManifest)
            {
                if (manifestFound ||
                    file.Role != ReleasePackageRole.Unknown ||
                    file.Executable ||
                    file.Length != source.Plan.ManifestLength ||
                    !file.Sha256.SequenceEqual(source.Plan.ManifestSha256))
                {
                    return false;
                }
                manifestFound = true;
            }
            else
            {
                if (!RequiredRoles.Contains(file.Role) ||
                    !HasExactRoleRoot(file.Role, file.RelativePath))
                {
                    return false;
                }
                rolesWithFiles.Add(file.Role);
            }

            string sourceFilePath;
            string targetFilePath;
            try
            {
                sourceFilePath = Path.GetFullPath(
                    Path.Combine(
                        sourcePath,
                        file.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
                targetFilePath = Path.GetFullPath(
                    Path.Combine(
                        targetPath,
                        file.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or
                    PathTooLongException)
            {
                return false;
            }

            if (!IsStrictDescendant(sourcePath, sourceFilePath) ||
                !IsStrictDescendant(targetPath, targetFilePath) ||
                !sourcePaths.Add(sourceFilePath) ||
                !targetPaths.Add(targetFilePath))
            {
                return false;
            }

            AddParentDirectories(file.RelativePath, directories);
            try
            {
                total = checked(total + file.Length);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (total >
                VerifiedReleaseArchiveExtractionService.MaximumExpandedBytes)
            {
                return false;
            }

            plans.Add(
                new VerifiedReleaseExtractedPublicationFilePlan(
                    file.Role,
                    file.RelativePath,
                    sourceFilePath,
                    targetFilePath,
                    file.Length,
                    file.Sha256,
                    file.Executable));
        }

        if (!manifestFound ||
            !rolesWithFiles.SetEquals(RequiredRoles) ||
            directories.Count != source.DirectoryCount ||
            total != source.ExpandedBytes)
        {
            return false;
        }

        filePlans = plans
            .OrderBy(plan => plan.RelativePath, StringComparer.Ordinal)
            .ToArray();
        directoryCount = directories.Count;
        publicationBytes = total;
        return true;
    }

    private static bool HasExactRoleRoot(
        ReleasePackageRole role,
        string relativePath)
    {
        string prefix = RoleDirectoryName(role) + "/";
        return relativePath.StartsWith(prefix, StringComparison.Ordinal) &&
            relativePath.Length > prefix.Length;
    }

    private static string RoleDirectoryName(ReleasePackageRole role) =>
        role switch
        {
            ReleasePackageRole.GatewayWeb => "gateway-web",
            ReleasePackageRole.Broker => "broker",
            ReleasePackageRole.AetherRemoteAgent => "aetherremote-agent",
            ReleasePackageRole.StationEngine => "station-engine",
            _ => throw new InvalidOperationException(
                "The extracted publication role is unsupported.")
        };

    private static void AddParentDirectories(
        string relativePath,
        ISet<string> directories)
    {
        string[] segments = relativePath.Split('/');
        if (segments.Length <= 1)
        {
            return;
        }

        string current = string.Empty;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            current = current.Length == 0
                ? segments[index]
                : $"{current}/{segments[index]}";
            directories.Add(current);
        }
    }

    private static bool IsStrictDescendant(string root, string path) =>
        path.StartsWith(
            root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsCanonicalAbsolutePath(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            !Path.IsPathFullyQualified(value))
        {
            return false;
        }
        try
        {
            return PathEquals(Path.GetFullPath(value), value);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsCanonicalReleaseIdentity(string value)
    {
        try
        {
            return string.Equals(
                InstallationReleaseIdentity.Parse(value),
                value,
                StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string CanonicalSemanticVersion(
        ReleaseSemanticVersion version) =>
        $"{version.Major}.{version.Minor}.{version.Patch}" +
        (version.Prerelease.Length == 0
            ? string.Empty
            : $"-{version.Prerelease}") +
        (version.BuildMetadata.Length == 0
            ? string.Empty
            : $"+{version.BuildMetadata}");

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool PathEquals(string? left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
