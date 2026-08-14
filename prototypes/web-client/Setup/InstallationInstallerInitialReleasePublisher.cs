using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Releases;

namespace AetherSDR.Web.Setup;

internal enum InstallationInstallerInitialReleaseOutcome
{
    Converged = 1,
    Missing = 2,
    Rejected = 3,
    Unknown = 4
}

internal sealed record InstallationInstallerInitialReleaseResult(
    InstallationInstallerInitialReleaseOutcome Outcome,
    string Code,
    string Summary);

internal sealed class InstallationInstallerInitialReleasePublisher
{
    private const string InventoryRoot =
        "/var/lib/aethersdr-installer/releases";
    private const int InventorySchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal async Task<InstallationInstallerInitialReleaseResult> InspectAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            return Rejected(
                "ubuntu-platform-unsupported",
                "Initial verified release publication requires Linux.");
        }

        VerifiedReleaseStagingReport? staging = request.VerifiedStaging;
        VerifiedReleaseInstallationPlan? verifiedPlan =
            request.VerifiedInstallationPlan;
        bool bindingProvided = staging is not null || verifiedPlan is not null;
        if (bindingProvided &&
            (staging is null || !EligibleStaging(request, staging)) &&
            (verifiedPlan is null || !EligiblePlan(request, verifiedPlan)))
        {
            return Rejected(
                "ubuntu-verified-release-unavailable",
                "Initial release publication requires the exact retained successful M8B verified release binding.");
        }

        if (!Directory.Exists(request.TargetReleasePath))
        {
            return Missing();
        }
        if (!SafeDirectory(request.TargetReleasePath))
        {
            return Rejected(
                "ubuntu-published-release-unsafe",
                "The initial published release root is unsafe.");
        }

        string inventoryPath = InventoryPath(request);
        if (!SafeRegularFile(inventoryPath))
        {
            return Rejected(
                "ubuntu-release-inventory-unavailable",
                "The published release lacks its exact verified file inventory.");
        }

        try
        {
            InstallationInstallerReleaseInventory? inventory =
                JsonSerializer.Deserialize<InstallationInstallerReleaseInventory>(
                    await File.ReadAllTextAsync(
                        inventoryPath,
                        cancellationToken),
                    JsonOptions);
            if (inventory is null ||
                inventory.SchemaVersion != InventorySchemaVersion ||
                inventory.SetupRevision != request.SetupRevision ||
                !string.Equals(
                    inventory.PlanId,
                    request.PlanId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    inventory.ReleaseIdentity,
                    request.ReleaseIdentity,
                    StringComparison.Ordinal) ||
                inventory.Files.Count is < 5 or >
                    VerifiedReleaseArchiveExtractionService
                        .MaximumExtractedFileCount ||
                inventory.DirectoryCount is < 4 or >
                    VerifiedReleaseArchiveExtractionService
                        .MaximumExtractedDirectoryCount)
            {
                return Rejected(
                    "ubuntu-release-inventory-invalid",
                    "The published release inventory does not match the exact installer transaction.");
            }

            bool exact = await VerifyInventoryAsync(
                request.TargetReleasePath,
                inventory,
                cancellationToken);
            return exact
                ? Converged()
                : Rejected(
                    "ubuntu-published-release-drift",
                    "The published immutable release differs from its verified inventory.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Unknown(
                "ubuntu-release-inventory-inspection-unknown",
                "The published release inventory could not be fully inspected.");
        }
    }

    [SupportedOSPlatform("linux")]
    internal async Task<InstallationInstallerInitialReleaseResult> PublishAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        InstallationInstallerInitialReleaseResult before =
            await InspectAsync(request, cancellationToken);
        if (before.Outcome ==
            InstallationInstallerInitialReleaseOutcome.Converged)
        {
            return before;
        }
        if (before.Outcome is
            InstallationInstallerInitialReleaseOutcome.Rejected or
            InstallationInstallerInitialReleaseOutcome.Unknown)
        {
            return before;
        }

        VerifiedReleaseInstallationPlan plan =
            request.VerifiedInstallationPlan ??
            request.VerifiedStaging?.StagedRelease?.Plan ??
            throw new InvalidOperationException(
                "The exact verified release plan is unavailable.");
        if (!InitialFilesystemEligible(plan, request))
        {
            return Rejected(
                "ubuntu-initial-release-state-ineligible",
                "Initial publication requires an absent current pointer and an empty target release slot.");
        }

        Func<CancellationToken, Task<ReleaseStatusReadResult>> virtualStatus =
            token =>
            {
                token.ThrowIfCancellationRequested();
                bool targetPresent = Directory.Exists(plan.TargetReleasePath);
                string[] identities = targetPresent
                    ? new[]
                        {
                            plan.InstalledReleaseIdentity,
                            plan.TargetReleaseIdentity
                        }
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray()
                    : [plan.InstalledReleaseIdentity];
                return Task.FromResult(new ReleaseStatusReadResult(
                    Succeeded: true,
                    ReleaseStatusFailureCode.None,
                    "The clean-install publication boundary supplied a stable virtual pre-activation status.",
                    SetupSchemaVersion: 1,
                    SetupRevision: plan.SetupRevision,
                    SetupComplete: true,
                    InstallationSetupLockMode.Complete,
                    InstallationSetupStep.Administrator,
                    plan.UpdateChannel,
                    plan.PinnedReleaseIdentity,
                    plan.InstallTransmitSupport,
                    ReleaseDirectoryPresent: true,
                    AvailableReleaseCount: identities.Length,
                    AvailableReleaseIdentities: identities,
                    CurrentPointerPresent: true,
                    ActiveReleaseIdentity: plan.InstalledReleaseIdentity,
                    RollbackCandidateKnown: false));
            };

        VerifiedReleaseStagingReport staging;
        if (request.VerifiedStaging is not null)
        {
            staging = request.VerifiedStaging;
        }
        else
        {
            staging = await new VerifiedReleaseStagingService(virtualStatus)
                .StageAsync(plan, cancellationToken);
            if (!staging.Succeeded || staging.StagedRelease is null)
            {
                return staging.CleanupRequired
                    ? Unknown(
                        "ubuntu-release-staging-reconciliation",
                        "Verified clean-install staging requires cleanup reconciliation.")
                    : Rejected(
                        "ubuntu-release-staging-rejected",
                        "The signed offline bundle could not be staged immutably.");
            }
        }

        VerifiedReleaseArchiveExtractionService extractionService =
            new(virtualStatus);
        VerifiedReleaseArchiveExtractionReport extraction =
            await extractionService.ExtractAsync(
                staging,
                cancellationToken);
        if (!extraction.Succeeded ||
            extraction.ExtractedRelease is null)
        {
            return extraction.CleanupRequired
                ? Unknown(
                    "ubuntu-release-extraction-reconciliation",
                    "Verified archive extraction requires cleanup reconciliation.")
                : Rejected(
                    "ubuntu-release-extraction-rejected",
                    "The retained verified release could not be safely extracted.");
        }

        VerifiedReleaseExtractedPublicationPlanCompositionResult composition =
            new VerifiedReleaseExtractedPublicationPlanComposer()
                .Compose(extraction);
        if (!composition.Succeeded || composition.Plan is null)
        {
            return Rejected(
                "ubuntu-release-publication-plan-rejected",
                "The verified extraction could not produce an exact atomic publication plan.");
        }

        VerifiedReleaseExtractedPublicationService publicationService =
            new(virtualStatus, Directory.Move);
        VerifiedReleaseExtractedPublicationReport publication =
            await publicationService.PublishAsync(
                composition,
                cancellationToken);
        if (!publication.Succeeded ||
            publication.PublishedRelease is null)
        {
            return publication.ReconciliationRequired
                ? Unknown(
                    "ubuntu-release-publication-reconciliation",
                    "Atomic initial release publication requires reconciliation.")
                : Rejected(
                    "ubuntu-release-publication-rejected",
                    "The verified extracted release was not published.");
        }

        try
        {
            await WriteInventoryAsync(
                request,
                composition.Plan,
                cancellationToken);
        }
        catch
        {
            return Unknown(
                "ubuntu-release-inventory-write-unknown",
                "The release was published but its durable verified inventory requires reconciliation.");
        }

        return await InspectAsync(request, cancellationToken);
    }

    private static bool EligibleStaging(
        InstallationInstallerUbuntuMutationRequest request,
        VerifiedReleaseStagingReport staging)
    {
        VerifiedStagedRelease? retained = staging.StagedRelease;
        if (!staging.Succeeded ||
            staging.FailureCode != VerifiedReleaseStagingFailureCode.None ||
            retained is null ||
            staging.SetupRevision != request.SetupRevision ||
            !string.Equals(
                staging.TargetReleaseIdentity,
                request.ReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                retained.Plan.TargetReleasePath,
                request.TargetReleasePath,
                StringComparison.Ordinal) ||
            !string.Equals(
                retained.StagingPath,
                request.ImmutableStagingPath,
                StringComparison.Ordinal) ||
            staging.PackageCount != retained.Plan.Packages.Count ||
            staging.StagedBytes != retained.StagedBytes ||
            !staging.ManifestStaged ||
            !staging.ImmutableStagingTree ||
            staging.TargetPublished ||
            staging.CurrentPointerChanged ||
            staging.CleanupRequired)
        {
            return false;
        }

        ReleaseManifestArchitecture expected =
            request.Architecture switch
            {
                InstallationInstallerArchitecture.LinuxX64 =>
                    ReleaseManifestArchitecture.LinuxX64,
                InstallationInstallerArchitecture.LinuxArm64 =>
                    ReleaseManifestArchitecture.LinuxArm64,
                _ => 0
            };
        return retained.Plan.Architecture == expected;
    }

    [SupportedOSPlatform("linux")]
    private static bool EligiblePlan(
        InstallationInstallerUbuntuMutationRequest request,
        VerifiedReleaseInstallationPlan plan)
    {
        ReleaseManifestArchitecture architecture =
            request.Architecture switch
            {
                InstallationInstallerArchitecture.LinuxX64 =>
                    ReleaseManifestArchitecture.LinuxX64,
                InstallationInstallerArchitecture.LinuxArm64 =>
                    ReleaseManifestArchitecture.LinuxArm64,
                _ => 0
            };
        return plan.SetupRevision == request.SetupRevision &&
            plan.Architecture == architecture &&
            string.Equals(
                plan.TargetReleaseIdentity,
                request.ReleaseIdentity,
                StringComparison.Ordinal) &&
            PathEquals(plan.TargetReleasePath, request.TargetReleasePath) &&
            plan.Packages.Count == 4;
    }

    [SupportedOSPlatform("linux")]
    private static bool InitialFilesystemEligible(
        VerifiedReleaseInstallationPlan plan,
        InstallationInstallerUbuntuMutationRequest request)
    {
        if (!PathEquals(plan.TargetReleasePath, request.TargetReleasePath) ||
            !SafeDirectory(plan.DeploymentRootPath) ||
            !SafeDirectory(plan.ReleaseRootPath) ||
            Directory.Exists(request.TargetReleasePath) ||
            File.Exists(request.TargetReleasePath) ||
            PathEntryExists("/opt/aethersdr/current"))
        {
            return false;
        }

        try
        {
            return !Directory.EnumerateFileSystemEntries(plan.ReleaseRootPath)
                .Any();
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task WriteInventoryAsync(
        InstallationInstallerUbuntuMutationRequest request,
        VerifiedReleaseExtractedPublicationPlan plan,
        CancellationToken cancellationToken)
    {
        InstallationInstallerReleaseInventory inventory = new(
            InventorySchemaVersion,
            request.PlanId,
            request.SetupRevision,
            request.ReleaseIdentity,
            plan.DirectoryCount,
            plan.Files.Select(file =>
                new InstallationInstallerReleaseInventoryFile(
                    file.RelativePath,
                    file.Length,
                    Convert.ToHexStringLower(file.Sha256),
                    file.Executable)).ToArray());
        string json = JsonSerializer.Serialize(inventory, JsonOptions) + "\n";
        string target = InventoryPath(request);
        DirectoryInfo parent = new(InventoryRoot);
        parent.Refresh();
        if (!parent.Exists || parent.LinkTarget is not null ||
            File.Exists(target) || Directory.Exists(target))
        {
            throw new InvalidOperationException(
                "The release inventory target is unsafe.");
        }

        string staged = target + ".tmp";
        if (File.Exists(staged) || Directory.Exists(staged))
        {
            throw new InvalidOperationException(
                "A prior release inventory staging file requires reconciliation.");
        }

        await using (FileStream stream = new(
            staged,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous |
                    FileOptions.WriteThrough,
                UnixCreateMode = UnixFileMode.UserRead |
                    UnixFileMode.UserWrite
            }))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                inventory,
                JsonOptions,
                cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.SetUnixFileMode(staged, UnixFileMode.UserRead);
        File.Move(staged, target);
    }

    [SupportedOSPlatform("linux")]
    private static async Task<bool> VerifyInventoryAsync(
        string root,
        InstallationInstallerReleaseInventory inventory,
        CancellationToken cancellationToken)
    {
        Dictionary<string, InstallationInstallerReleaseInventoryFile> expected =
            inventory.Files.ToDictionary(
                file => file.RelativePath,
                StringComparer.Ordinal);
        if (expected.Count != inventory.Files.Count ||
            expected.Keys.Any(path => !ReleasePackagePath.IsSafe(path)))
        {
            return false;
        }

        HashSet<string> actualFiles = new(StringComparer.Ordinal);
        HashSet<string> actualDirectories = new(StringComparer.Ordinal);
        Stack<(string Path, string Relative)> pending = new();
        pending.Push((root, string.Empty));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string directory, string relative) = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
                string name = Path.GetFileName(entry);
                string childRelative = relative.Length == 0
                    ? name
                    : relative + "/" + name;
                if (!ReleasePackagePath.IsSafe(childRelative))
                {
                    return false;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DirectoryInfo child = new(entry);
                    child.Refresh();
                    if (child.LinkTarget is not null ||
                        !actualDirectories.Add(childRelative))
                    {
                        return false;
                    }
                    pending.Push((entry, childRelative));
                    continue;
                }

                FileInfo file = new(entry);
                file.Refresh();
                if (!SafeRegularFile(entry) ||
                    !actualFiles.Add(childRelative) ||
                    !expected.TryGetValue(
                        childRelative,
                        out InstallationInstallerReleaseInventoryFile? item) ||
                    file.Length != item.Length)
                {
                    return false;
                }
                await using FileStream stream = new(
                    entry,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                string digest = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(
                        digest,
                        item.Sha256,
                        StringComparison.Ordinal) ||
                    Executable(entry) != item.Executable)
                {
                    return false;
                }
            }
        }

        return actualFiles.SetEquals(expected.Keys) &&
            actualDirectories.Count == inventory.DirectoryCount;
    }

    [SupportedOSPlatform("linux")]
    private static bool Executable(string path)
    {
        UnixFileMode mode = File.GetUnixFileMode(path);
        return (mode &
            (UnixFileMode.UserExecute |
             UnixFileMode.GroupExecute |
             UnixFileMode.OtherExecute)) != 0;
    }

    private static string InventoryPath(
        InstallationInstallerUbuntuMutationRequest request)
    {
        string identityHash = Convert.ToHexStringLower(
            SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    request.ReleaseIdentity)));
        return Path.Combine(
            InventoryRoot,
            identityHash + ".json");
    }

    [SupportedOSPlatform("linux")]
    private static bool SafeDirectory(string path)
    {
        try
        {
            DirectoryInfo directory = new(path);
            directory.Refresh();
            if (!directory.Exists ||
                directory.LinkTarget is not null ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode &
                (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool SafeRegularFile(string path)
    {
        try
        {
            FileInfo file = new(path);
            file.Refresh();
            if (!file.Exists ||
                file.LinkTarget is not null ||
                (file.Attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode &
                (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);

    private static InstallationInstallerInitialReleaseResult Converged() =>
        new(
            InstallationInstallerInitialReleaseOutcome.Converged,
            "ubuntu-release-converged",
            "The verified immutable release is published exactly.");

    private static InstallationInstallerInitialReleaseResult Missing() =>
        new(
            InstallationInstallerInitialReleaseOutcome.Missing,
            "ubuntu-release-missing",
            "The verified immutable release is not published.");

    private static InstallationInstallerInitialReleaseResult Rejected(
        string code,
        string summary) =>
        new(
            InstallationInstallerInitialReleaseOutcome.Rejected,
            code,
            summary);

    private static InstallationInstallerInitialReleaseResult Unknown(
        string code,
        string summary) =>
        new(
            InstallationInstallerInitialReleaseOutcome.Unknown,
            code,
            summary);

    private sealed record InstallationInstallerReleaseInventory(
        int SchemaVersion,
        string PlanId,
        long SetupRevision,
        string ReleaseIdentity,
        int DirectoryCount,
        IReadOnlyList<InstallationInstallerReleaseInventoryFile> Files);

    private sealed record InstallationInstallerReleaseInventoryFile(
        string RelativePath,
        long Length,
        string Sha256,
        bool Executable);
}
