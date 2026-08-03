using System.Buffers;
using System.Security;
using System.Security.Cryptography;

namespace AetherSDR.Web.Releases;

public enum LocalOfflineReleaseBundleFailureCode
{
    None = 0,
    InvalidBundleDirectory = 1,
    BundleDirectoryMissing = 2,
    UnsafeBundleEntry = 3,
    BundleNotImmutable = 4,
    MissingManifest = 5,
    UnexpectedBundleContents = 6,
    ManifestTooLarge = 7,
    PackageTooLarge = 8,
    BundleChangedDuringRead = 9,
    BundleReadFailed = 10,
    VerificationFailed = 11
}

public sealed record LocalOfflineReleaseBundleVerificationReport(
    bool Succeeded,
    LocalOfflineReleaseBundleFailureCode FailureCode,
    string Message,
    int PackageCount,
    long TotalPackageBytes,
    ReleaseManifestVerificationReport? Verification)
{
    internal static LocalOfflineReleaseBundleVerificationReport Failure(
        LocalOfflineReleaseBundleFailureCode failureCode,
        string message,
        int packageCount = 0,
        long totalPackageBytes = 0,
        ReleaseManifestVerificationReport? verification = null) =>
        new(
            false,
            failureCode,
            message,
            packageCount,
            totalPackageBytes,
            verification);

    internal static LocalOfflineReleaseBundleVerificationReport Success(
        int packageCount,
        long totalPackageBytes,
        ReleaseManifestVerificationReport verification) =>
        new(
            true,
            LocalOfflineReleaseBundleFailureCode.None,
            "The immutable local offline release bundle verified successfully.",
            packageCount,
            totalPackageBytes,
            verification);
}

public sealed record LocalOfflineReleaseBundleVerificationDiagnostics(
    bool Registered,
    bool DirectoryReadRegistered,
    bool ArchiveExtractionRegistered,
    bool NetworkDownloadRegistered,
    bool InstallationRegistered,
    bool ActivationRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered);

/// <summary>
/// Reads one pre-existing immutable local directory containing exactly one
/// release-manifest.json and four package files, snapshots the manifest and
/// package digests, and submits them to the trust-backed signed-manifest
/// verifier. It performs no download, extraction, write, installation,
/// activation, service, migration, backup, radio, watchdog, command, lease,
/// browser, or transmit operation.
/// </summary>
public sealed class LocalOfflineReleaseBundleVerificationService
{
    internal const string ManifestFileName = "release-manifest.json";
    internal const int RequiredPackageCount = 4;
    internal const int MaximumDirectoryCount = 16;
    internal const int MaximumBundlePathLength = 1024;

    private const UnixFileMode WritableUnixModes =
        UnixFileMode.UserWrite |
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;

    private readonly SignedReleaseManifestVerificationService
        m_manifestVerificationService;

    public LocalOfflineReleaseBundleVerificationService(
        SignedReleaseManifestVerificationService manifestVerificationService)
    {
        m_manifestVerificationService = manifestVerificationService ??
            throw new ArgumentNullException(nameof(manifestVerificationService));
        Snapshot = new LocalOfflineReleaseBundleVerificationDiagnostics(
            Registered: true,
            DirectoryReadRegistered: true,
            ArchiveExtractionRegistered: false,
            NetworkDownloadRegistered: false,
            InstallationRegistered: false,
            ActivationRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false);
    }

    public LocalOfflineReleaseBundleVerificationDiagnostics Snapshot { get; }

    public LocalOfflineReleaseBundleVerificationReport VerifyDirectory(
        string bundleDirectory,
        ReleaseManifestVerificationContext context)
    {
        if (context is null)
        {
            return LocalOfflineReleaseBundleVerificationReport.Failure(
                LocalOfflineReleaseBundleFailureCode.BundleReadFailed,
                "The local offline release bundle verification context is missing.");
        }

        SignedReleaseManifestVerificationServiceDiagnostics readiness =
            m_manifestVerificationService.Snapshot;
        if (!readiness.LocalVerificationAvailable)
        {
            bool disabled = string.Equals(
                readiness.Reason,
                "disabled",
                StringComparison.Ordinal);
            ReleaseManifestVerificationReport verification =
                ReleaseManifestVerificationReport.Failure(
                    disabled
                        ? ReleaseManifestFailureCode.VerificationTrustDisabled
                        : ReleaseManifestFailureCode.VerificationTrustUnavailable,
                    disabled
                        ? "Signed release manifest verification trust is disabled."
                        : "Signed release manifest verification trust is unavailable.");
            return LocalOfflineReleaseBundleVerificationReport.Failure(
                LocalOfflineReleaseBundleFailureCode.VerificationFailed,
                "The immutable local offline release bundle cannot be verified because production trust is unavailable.",
                verification: verification);
        }

        try
        {
            BundleSnapshot bundle = ReadBundle(bundleDirectory);
            ReleaseManifestVerificationReport verification =
                m_manifestVerificationService.VerifyLocal(
                    bundle.Manifest,
                    bundle.Packages,
                    context);
            return verification.Succeeded
                ? LocalOfflineReleaseBundleVerificationReport.Success(
                    bundle.Packages.Length,
                    bundle.TotalPackageBytes,
                    verification)
                : LocalOfflineReleaseBundleVerificationReport.Failure(
                    LocalOfflineReleaseBundleFailureCode.VerificationFailed,
                    "The immutable local offline release bundle failed signed-manifest verification.",
                    bundle.Packages.Length,
                    bundle.TotalPackageBytes,
                    verification);
        }
        catch (BundleReadException exception)
        {
            return LocalOfflineReleaseBundleVerificationReport.Failure(
                exception.FailureCode,
                exception.Message);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or CryptographicException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return LocalOfflineReleaseBundleVerificationReport.Failure(
                LocalOfflineReleaseBundleFailureCode.BundleReadFailed,
                "The immutable local offline release bundle could not be read.");
        }
    }

    private static BundleSnapshot ReadBundle(string bundleDirectory)
    {
        string rootPath = ValidateBundlePath(bundleDirectory);
        DirectoryInfo root = new(rootPath);
        root.Refresh();
        if (!root.Exists)
        {
            throw Failure(
                LocalOfflineReleaseBundleFailureCode.BundleDirectoryMissing,
                "The local offline release bundle directory does not exist.");
        }
        ValidateDirectory(root);

        List<BundleFile> files = [];
        Stack<DirectoryInfo> pending = new();
        pending.Push(root);
        int directoryCount = 0;
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        HashSet<string> visitedDirectories = new(pathComparer);

        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            ValidateDirectory(directory);
            if (!visitedDirectories.Add(directory.FullName) ||
                ++directoryCount > MaximumDirectoryCount)
            {
                throw Failure(
                    LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry,
                    "The local offline release bundle directory structure is unsafe or exceeds its bound.");
            }

            FileSystemInfo[] entries = directory.GetFileSystemInfos();
            if (directory.FullName != root.FullName && entries.Length == 0)
            {
                throw Failure(
                    LocalOfflineReleaseBundleFailureCode.UnexpectedBundleContents,
                    "The local offline release bundle contains an empty directory.");
            }

            foreach (FileSystemInfo entry in entries)
            {
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    entry.LinkTarget is not null)
                {
                    throw Failure(
                        LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry,
                        "The local offline release bundle contains a symbolic link or reparse point.");
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    string relativeDirectory = RelativePath(rootPath, childDirectory.FullName);
                    if (!ReleasePackagePath.IsSafe(relativeDirectory))
                    {
                        throw Failure(
                            LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry,
                            "The local offline release bundle contains an unsafe directory path.");
                    }
                    pending.Push(childDirectory);
                    continue;
                }

                if (entry is not FileInfo file ||
                    (file.Attributes & FileAttributes.Directory) != 0)
                {
                    throw Failure(
                        LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry,
                        "The local offline release bundle contains a non-regular entry.");
                }

                string relativePath = RelativePath(rootPath, file.FullName);
                if (!string.Equals(
                        relativePath,
                        ManifestFileName,
                        StringComparison.Ordinal) &&
                    !ReleasePackagePath.IsSafe(relativePath))
                {
                    throw Failure(
                        LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry,
                        "The local offline release bundle contains an unsafe file path.");
                }

                files.Add(new BundleFile(relativePath, file));
                if (files.Count > RequiredPackageCount + 1)
                {
                    throw Failure(
                        LocalOfflineReleaseBundleFailureCode.UnexpectedBundleContents,
                        "The local offline release bundle contains unexpected files.");
                }
            }
        }

        BundleFile? manifestFile = files.SingleOrDefault(file =>
            string.Equals(
                file.RelativePath,
                ManifestFileName,
                StringComparison.Ordinal));
        if (manifestFile is null)
        {
            throw Failure(
                LocalOfflineReleaseBundleFailureCode.MissingManifest,
                "The local offline release bundle is missing release-manifest.json.");
        }

        BundleFile[] packageFiles = files
            .Where(file => !ReferenceEquals(file, manifestFile))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (packageFiles.Length != RequiredPackageCount)
        {
            throw Failure(
                LocalOfflineReleaseBundleFailureCode.UnexpectedBundleContents,
                "The local offline release bundle must contain exactly four package files.");
        }

        byte[] manifest = ReadManifest(manifestFile.File);
        LocalImmutableReleasePackage[] packages =
            new LocalImmutableReleasePackage[packageFiles.Length];
        long totalPackageBytes = 0;
        for (int index = 0; index < packageFiles.Length; index++)
        {
            packages[index] = ReadPackage(packageFiles[index]);
            try
            {
                totalPackageBytes = checked(
                    totalPackageBytes + packages[index].Length);
            }
            catch (OverflowException)
            {
                throw Failure(
                    LocalOfflineReleaseBundleFailureCode.PackageTooLarge,
                    "The local offline release bundle package total exceeds its bound.");
            }
        }

        ValidateDirectory(root);
        return new BundleSnapshot(manifest, packages, totalPackageBytes);
    }

    private static string ValidateBundlePath(string? value)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length is 0 or > MaximumBundlePathLength ||
            !string.Equals(value, path, StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw Failure(
                LocalOfflineReleaseBundleFailureCode.InvalidBundleDirectory,
                "The local offline release bundle requires one canonical absolute directory path.");
        }

        string fullPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(path, fullPath, comparison))
        {
            throw Failure(
                LocalOfflineReleaseBundleFailureCode.InvalidBundleDirectory,
                "The local offline release bundle path must not contain relative segments.");
        }
        return fullPath;
    }

    private static string RelativePath(string rootPath, string entryPath)
    {
        string relative = Path.GetRelativePath(rootPath, entryPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            relative = relative.Replace(Path.AltDirectorySeparatorChar, '/');
        }
        return relative;
    }

    private static void ValidateDirectory(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw Failure(
                LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry,
                "The local offline release bundle requires regular non-symlink directories.");
        }
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(directory.FullName) & WritableUnixModes) != 0)
        {
            throw Failure(
                LocalOfflineReleaseBundleFailureCode.BundleNotImmutable,
                "The local offline release bundle directories must not be writable.");
        }
    }

    private static FileState ValidateFile(
        FileInfo file,
        long maximumLength,
        LocalOfflineReleaseBundleFailureCode oversizedCode,
        string oversizedMessage)
    {
        file.Refresh();
        if (!file.Exists ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.LinkTarget is not null)
        {
            throw Failure(
                LocalOfflineReleaseBundleFailureCode.UnsafeBundleEntry,
                "The local offline release bundle requires regular non-symlink files.");
        }
        if (file.Length is <= 0 || file.Length > maximumLength)
        {
            throw Failure(oversizedCode, oversizedMessage);
        }
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(file.FullName) & WritableUnixModes) != 0)
        {
            throw Failure(
                LocalOfflineReleaseBundleFailureCode.BundleNotImmutable,
                "The local offline release bundle files must not be writable.");
        }

        return new FileState(file.Length, file.LastWriteTimeUtc);
    }

    private static byte[] ReadManifest(FileInfo file)
    {
        FileState before = ValidateFile(
            file,
            SignedReleaseManifestJson.MaximumManifestBytes,
            LocalOfflineReleaseBundleFailureCode.ManifestTooLarge,
            "The local offline release manifest is empty or exceeds its bound.");
        byte[] content = new byte[checked((int)before.Length)];
        using FileStream stream = OpenRead(file.FullName);
        if (stream.Length != before.Length)
        {
            throw Changed();
        }
        stream.ReadExactly(content);
        ValidateUnchanged(file, before, stream.Length);
        return content;
    }

    private static LocalImmutableReleasePackage ReadPackage(BundleFile package)
    {
        FileState before = ValidateFile(
            package.File,
            SignedReleaseManifestVerifier.MaximumDeclaredPackageLength,
            LocalOfflineReleaseBundleFailureCode.PackageTooLarge,
            "A local offline release package is empty or exceeds its bound.");
        using FileStream stream = OpenRead(package.File.FullName);
        if (stream.Length != before.Length)
        {
            throw Changed();
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long bytesRead = 0;
        try
        {
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                bytesRead = checked(bytesRead + read);
                if (bytesRead > before.Length)
                {
                    throw Changed();
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (bytesRead != before.Length)
        {
            throw Changed();
        }
        byte[] digest = hash.GetHashAndReset();
        ValidateUnchanged(package.File, before, stream.Length);
        return new LocalImmutableReleasePackage(
            package.RelativePath,
            before.Length,
            digest);
    }

    private static FileStream OpenRead(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 128 * 1024,
                Options = FileOptions.SequentialScan
            });

    private static void ValidateUnchanged(
        FileInfo file,
        FileState before,
        long streamLength)
    {
        FileState after = ValidateFile(
            file,
            before.Length,
            LocalOfflineReleaseBundleFailureCode.BundleChangedDuringRead,
            "The local offline release bundle changed while it was being read.");
        if (streamLength != before.Length ||
            after.Length != before.Length ||
            after.LastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            throw Changed();
        }
    }

    private static BundleReadException Changed() =>
        Failure(
            LocalOfflineReleaseBundleFailureCode.BundleChangedDuringRead,
            "The local offline release bundle changed while it was being read.");

    private static BundleReadException Failure(
        LocalOfflineReleaseBundleFailureCode failureCode,
        string message) =>
        new(failureCode, message);

    private sealed record BundleFile(string RelativePath, FileInfo File);

    private sealed record BundleSnapshot(
        byte[] Manifest,
        LocalImmutableReleasePackage[] Packages,
        long TotalPackageBytes);

    private readonly record struct FileState(
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed class BundleReadException(
        LocalOfflineReleaseBundleFailureCode failureCode,
        string message) : Exception(message)
    {
        public LocalOfflineReleaseBundleFailureCode FailureCode { get; } =
            failureCode;
    }
}
