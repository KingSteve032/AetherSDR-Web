using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Data.Sqlite;

namespace AetherSDR.Web.Operations;

public sealed record InstallationBackupSummary(
    int SchemaVersion,
    string BackupId,
    DateTimeOffset CreatedAt,
    long SetupRevision,
    string CurrentReleaseIdentity,
    string RollbackReleaseIdentity,
    bool RollbackReleaseIdentityKnown,
    int ProtectedFileCount,
    long ProtectedPlaintextBytes,
    long EncryptedBackupBytes,
    IReadOnlyList<string> IncludedAuthorities,
    IReadOnlyList<string> ExternalDependencies);

public sealed record InstallationRestoreSummary(
    int SchemaVersion,
    string BackupId,
    DateTimeOffset BackupCreatedAt,
    long SetupRevision,
    string CurrentReleaseIdentity,
    string RollbackReleaseIdentity,
    bool RollbackReleaseIdentityKnown,
    int RestoredFileCount,
    bool ReplacementHostCompatible,
    IReadOnlyList<string> ExternalDependencies);

public sealed record InstallationBackupReadiness(
    bool Ready,
    string Code,
    string Message,
    long SetupRevision,
    bool SetupComplete,
    bool ConfigurationAvailable,
    bool StateAvailable,
    bool IdentitySnapshotAvailable,
    bool SecretsAvailable,
    bool BackupDirectoryAvailable,
    bool CurrentReleaseKnown,
    bool RollbackReleaseKnown,
    DateTimeOffset? LatestBackupCreatedAt,
    long? LatestBackupAgeSeconds,
    IReadOnlyList<string> ExternalDependencies);

/// <summary>
/// Creates and restores one encrypted, authenticated backup of installation-owned
/// durable authority. Backup contents are logical role/relative-path entries so a
/// replacement host can restore into its own validated InstallationPaths. Release
/// binaries are intentionally not copied; only current/rollback identities are
/// retained so signed release packages remain independently verifiable artifacts.
/// </summary>
public sealed class InstallationBackupService
{
    public const int SchemaVersion = 1;
    public const string BackupExtension = ".aebak";
    public const int MinimumPassphraseLength = 16;
    public const int MaximumPassphraseLength = 512;
    internal const int MaximumEntryCount = 8192;
    internal const long MaximumFileBytes = 64L * 1024 * 1024;
    internal const long MaximumPlaintextBytes = 256L * 1024 * 1024;
    internal const int Pbkdf2Iterations = 600_000;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const int HeaderBytes = 8 + 4 + 4 + SaltBytes + NonceBytes + 8;
    private const string RestoreJournalFileName = ".restore-active.json";
    private static readonly byte[] Magic = "AETHBKP1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly InstallationPaths m_paths;
    private readonly InstallationSetupStore m_setupStore;
    private readonly ReleaseInstallationStatusReader m_releaseStatusReader;
    private readonly TimeProvider m_timeProvider;
    private readonly SemaphoreSlim m_gate = new(1, 1);

    public InstallationBackupService(
        InstallationPaths paths,
        InstallationSetupStore setupStore,
        ReleaseInstallationStatusReader releaseStatusReader,
        TimeProvider? timeProvider = null)
    {
        m_paths = paths ?? throw new ArgumentNullException(nameof(paths));
        InstallationPaths.Validate(m_paths);
        m_setupStore = setupStore ?? throw new ArgumentNullException(nameof(setupStore));
        m_releaseStatusReader = releaseStatusReader ??
            throw new ArgumentNullException(nameof(releaseStatusReader));
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InstallationBackupReadiness> InspectReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        InstallationSetupState? setup = null;
        try
        {
            setup = await m_setupStore.LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or InvalidDataException or
            InvalidOperationException or IOException or UnauthorizedAccessException)
        {
        }

        bool setupComplete = setup?.Lock.Mode == InstallationSetupLockMode.Complete;
        bool configuration = IsSafeDirectory(m_paths.ConfigurationDirectory);
        bool state = IsSafeDirectory(m_paths.StateDirectory);
        bool secrets = IsSafeDirectory(m_paths.SecretDirectory);
        bool backupDirectory = EnsureBackupDirectory(readOnly: true);
        bool identity = setup?.Topology is not null &&
            (!InstallationTopologyProfile.For(setup.Topology.Value).GatewayRunsHere ||
             IsSafeRegularFile(m_paths.IdentityDatabasePath));
        ReleaseStatusReadResult release =
            await m_releaseStatusReader.ReadAsync(cancellationToken);
        (string rollbackIdentity, bool rollbackKnown) = ReadRollbackIdentity();
        DateTimeOffset? latest = FindLatestBackupTimestamp();
        long? age = latest is null
            ? null
            : Math.Max(
                0,
                (long)(m_timeProvider.GetUtcNow() - latest.Value).TotalSeconds);
        IReadOnlyList<string> external = DetermineExternalDependencies(setup);

        bool ready = setupComplete && configuration && state && secrets &&
            backupDirectory && identity && release.Succeeded &&
            release.CurrentPointerPresent &&
            !string.IsNullOrEmpty(release.ActiveReleaseIdentity);
        string code = ready ? "ready" : "backup-prerequisites-failed";
        string message = ready
            ? "Durable installation authority is available for encrypted backup."
            : "One or more durable installation backup prerequisites are unavailable.";
        return new InstallationBackupReadiness(
            ready,
            code,
            message,
            setup?.Revision ?? 0,
            setupComplete,
            configuration,
            state,
            identity,
            secrets,
            backupDirectory,
            release.Succeeded && release.CurrentPointerPresent,
            rollbackKnown,
            latest,
            age,
            external);
    }

    public async Task<(string Path, InstallationBackupSummary Summary)> CreateAsync(
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        ValidatePassphrase(passphrase);
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            if (!EnsureBackupDirectory(readOnly: false))
            {
                throw new InvalidOperationException(
                    "The configured backup directory is unavailable or unsafe.");
            }
            InstallationSetupState setup = await m_setupStore.LoadAsync(cancellationToken);
            InstallationSetupStateValidator.Validate(setup);
            if (setup.Lock.Mode != InstallationSetupLockMode.Complete ||
                setup.Topology is null)
            {
                throw new InvalidOperationException(
                    "Encrypted backup requires completed installation setup.");
            }

            InstallationBackupReadiness readiness =
                await InspectReadinessAsync(cancellationToken);
            if (!readiness.Ready)
            {
                throw new InvalidOperationException(readiness.Message);
            }
            ReleaseStatusReadResult release =
                await m_releaseStatusReader.ReadAsync(cancellationToken);
            if (!release.Succeeded || !release.CurrentPointerPresent ||
                string.IsNullOrEmpty(release.ActiveReleaseIdentity))
            {
                throw new InvalidOperationException(
                    "Encrypted backup requires one validated active release identity.");
            }

            string backupId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            DateTimeOffset createdAt = m_timeProvider.GetUtcNow();
            (string rollbackIdentity, bool rollbackKnown) = ReadRollbackIdentity();
            List<InstallationBackupRoot> roots = [];
            HashSet<string> coveredRoots = new(PathComparer);
            foreach ((InstallationBackupRootRole role, string root) in GetProtectedRoots())
            {
                string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                if (coveredRoots.Any(existing => IsDescendant(normalized, existing)))
                {
                    continue;
                }
                roots.Add(await CaptureRootAsync(
                    role,
                    normalized,
                    setup,
                    cancellationToken));
                coveredRoots.Add(normalized);
            }

            InstallationBackupExactFile? managedProxy =
                await CaptureManagedProxyConfigurationAsync(cancellationToken);
            int fileCount = roots.Sum(root => root.Files.Count) +
                (managedProxy is null ? 0 : 1);
            long plaintextBytes = roots.Sum(root => root.Files.Sum(file => file.Length)) +
                (managedProxy?.Length ?? 0);
            if (fileCount > MaximumEntryCount || plaintextBytes > MaximumPlaintextBytes)
            {
                throw new InvalidDataException(
                    "The durable installation backup exceeds the supported bound.");
            }

            InstallationBackupPayload payload = new(
                SchemaVersion,
                backupId,
                createdAt,
                setup.SchemaVersion,
                setup.Revision,
                setup.Topology.Value,
                release.ActiveReleaseIdentity,
                rollbackIdentity,
                rollbackKnown,
                roots,
                managedProxy,
                DetermineExternalDependencies(setup));
            byte[] plaintext = CompressPayload(payload);
            try
            {
                byte[] encrypted = EncryptPayload(plaintext, passphrase);
                try
                {
                    string finalPath = Path.Combine(
                        m_paths.BackupDirectory,
                        $"aethersdr-{createdAt:yyyyMMddTHHmmssZ}-{backupId}{BackupExtension}");
                    await WriteAtomicBackupAsync(finalPath, encrypted, cancellationToken);
                    InstallationBackupSummary summary = new(
                        SchemaVersion,
                        backupId,
                        createdAt,
                        setup.Revision,
                        release.ActiveReleaseIdentity,
                        rollbackIdentity,
                        rollbackKnown,
                        fileCount,
                        plaintextBytes,
                        encrypted.LongLength,
                        IncludedAuthorities(setup),
                        payload.ExternalDependencies);
                    return (finalPath, summary);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encrypted);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<InstallationBackupSummary> InspectAsync(
        string backupPath,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        ValidatePassphrase(passphrase);
        string canonical = ValidateBackupInputPath(backupPath);
        byte[] encrypted = await ReadBoundedFileAsync(
            canonical,
            MaximumPlaintextBytes + 32L * 1024 * 1024,
            cancellationToken);
        try
        {
            byte[] plaintext = DecryptPayload(encrypted, passphrase);
            try
            {
                InstallationBackupPayload payload = DecompressPayload(plaintext);
                ValidatePayload(payload);
                return SummaryFrom(payload, encrypted.LongLength);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task<InstallationRestoreSummary> RestoreAsync(
        string backupPath,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        ValidatePassphrase(passphrase);
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            if (!EnsureBackupDirectory(readOnly: false))
            {
                throw new InvalidOperationException(
                    "The configured backup directory is unavailable or unsafe.");
            }
            RecoverPendingRestore();
            string canonical = ValidateBackupInputPath(backupPath);
            byte[] encrypted = await ReadBoundedFileAsync(
                canonical,
                MaximumPlaintextBytes + 32L * 1024 * 1024,
                cancellationToken);
            InstallationBackupPayload payload;
            try
            {
                byte[] plaintext = DecryptPayload(encrypted, passphrase);
                try
                {
                    payload = DecompressPayload(plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
            ValidatePayload(payload);
            ValidateRestoreReleaseCompatibility(payload);

            string transactionId = payload.BackupId;
            List<RestoreRootTransaction> transactions = [];
            try
            {
                foreach (InstallationBackupRoot root in payload.Roots)
                {
                    string target = TargetForRole(root.Role);
                    RestoreRootTransaction transaction = await StageRootAsync(
                        root,
                        target,
                        transactionId,
                        cancellationToken);
                    transactions.Add(transaction);
                }
                RestoreExactFileTransaction? proxyTransaction = null;
                if (payload.ManagedProxyConfiguration is not null)
                {
                    proxyTransaction = await StageExactFileAsync(
                        payload.ManagedProxyConfiguration,
                        ManagedCaddyConfigurationPath(),
                        transactionId,
                        cancellationToken);
                }
                RestorePointerTransaction pointerTransaction =
                    CreatePointerTransaction(payload.CurrentReleaseIdentity, transactionId);
                WriteRestoreJournal(new RestoreJournal(
                    1,
                    payload.BackupId,
                    RestoreJournalPhase.Prepared,
                    transactions.Select(ToJournalRoot).ToArray(),
                    proxyTransaction is null ? null : ToJournalFile(proxyTransaction),
                    pointerTransaction));

                foreach (RestoreRootTransaction transaction in transactions)
                {
                    ApplyRootTransaction(transaction);
                }
                if (proxyTransaction is not null)
                {
                    ApplyExactFileTransaction(proxyTransaction);
                }

                ApplyCurrentPointer(pointerTransaction);
                WriteRestoreJournal(new RestoreJournal(
                    1,
                    payload.BackupId,
                    RestoreJournalPhase.Committed,
                    transactions.Select(ToJournalRoot).ToArray(),
                    proxyTransaction is null ? null : ToJournalFile(proxyTransaction),
                    pointerTransaction));
                foreach (RestoreRootTransaction transaction in transactions)
                {
                    CompleteRootTransaction(transaction);
                }
                if (proxyTransaction is not null)
                {
                    CompleteExactFileTransaction(proxyTransaction);
                }
                CompletePointerTransaction(pointerTransaction);
                DeleteRestoreJournal();

                int restoredFiles = payload.Roots.Sum(root => root.Files.Count) +
                    (payload.ManagedProxyConfiguration is null ? 0 : 1);
                return new InstallationRestoreSummary(
                    SchemaVersion,
                    payload.BackupId,
                    payload.CreatedAt,
                    payload.SetupRevision,
                    payload.CurrentReleaseIdentity,
                    payload.RollbackReleaseIdentity,
                    payload.RollbackReleaseIdentityKnown,
                    restoredFiles,
                    ReplacementHostCompatible: true,
                    payload.ExternalDependencies);
            }
            catch
            {
                try
                {
                    RecoverPendingRestore();
                }
                catch
                {
                    // Preserve the original restore failure. The durable journal
                    // remains for the next explicit restore/recovery attempt.
                }
                throw;
            }
        }
        finally
        {
            m_gate.Release();
        }
    }

    private async Task<InstallationBackupRoot> CaptureRootAsync(
        InstallationBackupRootRole role,
        string root,
        InstallationSetupState setup,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Required durable backup root '{role}' is not available.");
        }
        ValidateDirectory(root);
        List<InstallationBackupFile> files = [];
        List<InstallationBackupDirectory> capturedDirectories = [];
        Queue<string> directories = new();
        directories.Enqueue(root);
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = directories.Dequeue();
            string directoryRelative = Path.GetRelativePath(root, current);
            string canonicalDirectoryRelative = directoryRelative == "."
                ? string.Empty
                : directoryRelative.Replace(Path.DirectorySeparatorChar, '/');
            if (!string.IsNullOrEmpty(canonicalDirectoryRelative))
            {
                ValidateRelativePath(canonicalDirectoryRelative);
            }
            capturedDirectories.Add(new InstallationBackupDirectory(
                canonicalDirectoryRelative,
                OperatingSystem.IsLinux()
                    ? (int)File.GetUnixFileMode(current)
                    : null,
                LogicalOwner(role, canonicalDirectoryRelative, setup)));
            foreach (string entry in Directory.EnumerateFileSystemEntries(current)
                .OrderBy(value => value, StringComparer.Ordinal))
            {
                if (ShouldExclude(entry, root))
                {
                    continue;
                }
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Durable backup roots may not contain symbolic links or reparse points.");
                }
                if (Directory.Exists(entry))
                {
                    ValidateDirectory(entry);
                    directories.Enqueue(entry);
                    continue;
                }
                if (!File.Exists(entry))
                {
                    throw new InvalidDataException(
                        "A durable backup entry changed type while it was being read.");
                }
                if (files.Count >= MaximumEntryCount)
                {
                    throw new InvalidDataException(
                        "The durable installation backup contains too many files.");
                }
                string relative = Path.GetRelativePath(root, entry);
                ValidateRelativePath(relative);
                byte[] content;
                if (PathEquals(entry, m_paths.IdentityDatabasePath) &&
                    InstallationTopologyProfile.For(setup.Topology!.Value).GatewayRunsHere)
                {
                    content = await SnapshotIdentityDatabaseAsync(cancellationToken);
                }
                else if (PathEquals(entry, m_paths.IdentityDatabasePath + "-wal") ||
                         PathEquals(entry, m_paths.IdentityDatabasePath + "-shm"))
                {
                    continue;
                }
                else
                {
                    content = await ReadStableFileAsync(entry, cancellationToken);
                }
                UnixFileMode? mode = OperatingSystem.IsLinux()
                    ? File.GetUnixFileMode(entry)
                    : null;
                string canonicalRelative =
                    relative.Replace(Path.DirectorySeparatorChar, '/');
                files.Add(new InstallationBackupFile(
                    canonicalRelative,
                    content.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(content)),
                    mode is null ? null : (int)mode.Value,
                    LogicalOwner(role, canonicalRelative, setup),
                    Convert.ToBase64String(content)));
                CryptographicOperations.ZeroMemory(content);
            }
        }
        return new InstallationBackupRoot(
            role,
            Present: true,
            capturedDirectories,
            files);
    }

    private async Task<byte[]> SnapshotIdentityDatabaseAsync(
        CancellationToken cancellationToken)
    {
        if (!IsSafeRegularFile(m_paths.IdentityDatabasePath))
        {
            throw new InvalidDataException(
                "The local identity database is not a safe regular file.");
        }
        string tempRoot = Directory.Exists("/dev/shm") && OperatingSystem.IsLinux()
            ? "/dev/shm"
            : m_paths.BackupDirectory;
        string snapshot = Path.Combine(
            tempRoot,
            $".aethersdr-identity-backup-{Guid.NewGuid():N}.db");
        try
        {
            string sourceConnection = new SqliteConnectionStringBuilder
            {
                DataSource = m_paths.IdentityDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private
            }.ToString();
            string destinationConnection = new SqliteConnectionStringBuilder
            {
                DataSource = snapshot,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString();
            await using SqliteConnection source = new(sourceConnection);
            await using SqliteConnection destination = new(destinationConnection);
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
            await destination.CloseAsync();
            await source.CloseAsync();
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    snapshot,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return await ReadStableFileAsync(snapshot, cancellationToken);
        }
        finally
        {
            if (File.Exists(snapshot))
            {
                File.Delete(snapshot);
            }
        }
    }

    private async Task<InstallationBackupExactFile?> CaptureManagedProxyConfigurationAsync(
        CancellationToken cancellationToken)
    {
        string stateMarker = ManagedCaddyMarkerPath();
        string target = ManagedCaddyConfigurationPath();
        if (!File.Exists(stateMarker) || !File.Exists(target))
        {
            return null;
        }
        if (!IsSafeRegularFile(stateMarker) || !IsSafeRegularFile(target))
        {
            throw new InvalidDataException(
                "Installer-owned managed proxy state is unsafe.");
        }
        string expected = (await File.ReadAllTextAsync(stateMarker, cancellationToken)).Trim();
        if (expected.Length != 64 || expected.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "Installer-owned managed proxy state has an invalid digest marker.");
        }
        byte[] content = await ReadStableFileAsync(target, cancellationToken);
        string actual = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(content);
            throw new InvalidDataException(
                "Managed proxy configuration does not match installer-owned state.");
        }
        UnixFileMode? mode = OperatingSystem.IsLinux()
            ? File.GetUnixFileMode(target)
            : null;
        InstallationBackupExactFile result = new(
            InstallationBackupExactFileRole.ManagedProxyConfiguration,
            content.LongLength,
            actual,
            mode is null ? null : (int)mode.Value,
            Convert.ToBase64String(content));
        CryptographicOperations.ZeroMemory(content);
        return result;
    }

    private IEnumerable<(InstallationBackupRootRole Role, string Path)> GetProtectedRoots()
    {
        yield return (InstallationBackupRootRole.Configuration, m_paths.ConfigurationDirectory);
        yield return (InstallationBackupRootRole.State, m_paths.StateDirectory);
        if (!IsDescendant(m_paths.SecretDirectory, m_paths.StateDirectory))
        {
            yield return (InstallationBackupRootRole.Secrets, m_paths.SecretDirectory);
        }
        string proxyState = ManagedProxyStateDirectory();
        if (Directory.Exists(proxyState) &&
            !IsDescendant(proxyState, m_paths.StateDirectory) &&
            !IsDescendant(proxyState, m_paths.ConfigurationDirectory))
        {
            yield return (InstallationBackupRootRole.ManagedProxyState, proxyState);
        }
    }

    private bool ShouldExclude(string path, string root)
    {
        foreach (string excluded in new[]
        {
            m_paths.ReleaseDirectory,
            m_paths.BackupDirectory,
            m_paths.LogDirectory,
            m_paths.ReleaseDownloadDirectory
        })
        {
            if (IsDescendant(path, excluded) || PathEquals(path, excluded))
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] CompressPayload(InstallationBackupPayload payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        try
        {
            if (json.LongLength > MaximumPlaintextBytes)
            {
                throw new InvalidDataException(
                    "The durable installation backup payload is too large.");
            }
            using MemoryStream output = new();
            using (BrotliStream brotli = new(
                output,
                CompressionLevel.SmallestSize,
                leaveOpen: true))
            {
                brotli.Write(json);
            }
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static InstallationBackupPayload DecompressPayload(byte[] compressed)
    {
        using MemoryStream input = new(compressed, writable: false);
        using BrotliStream brotli = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaximumPlaintextBytes)
            {
                throw new InvalidDataException(
                    "The decrypted backup payload exceeds the supported bound.");
            }
            output.Write(buffer, 0, read);
        }
        CryptographicOperations.ZeroMemory(buffer);
        InstallationBackupPayload? payload = JsonSerializer.Deserialize<InstallationBackupPayload>(
            output.ToArray(),
            JsonOptions);
        return payload ?? throw new InvalidDataException(
            "The decrypted backup payload is missing.");
    }

    private static byte[] EncryptPayload(byte[] plaintext, string passphrase)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagBytes];
        byte[] header = BuildHeader(salt, nonce, ciphertext.LongLength);
        try
        {
            using AesGcm aes = new(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, header);
            byte[] result = new byte[header.Length + ciphertext.Length + tag.Length];
            Buffer.BlockCopy(header, 0, result, 0, header.Length);
            Buffer.BlockCopy(ciphertext, 0, result, header.Length, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, header.Length + ciphertext.Length, tag.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(header);
        }
    }

    private static byte[] DecryptPayload(byte[] encrypted, string passphrase)
    {
        if (encrypted.Length < HeaderBytes + TagBytes)
        {
            throw new InvalidDataException("The encrypted backup is truncated.");
        }
        ReadOnlySpan<byte> header = encrypted.AsSpan(0, HeaderBytes);
        if (!header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The encrypted backup magic is invalid.");
        }
        int schema = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
        long ciphertextLength = BinaryPrimitives.ReadInt64LittleEndian(
            header.Slice(HeaderBytes - 8, 8));
        if (schema != SchemaVersion || iterations != Pbkdf2Iterations ||
            ciphertextLength < 1 ||
            ciphertextLength > MaximumPlaintextBytes + 32L * 1024 * 1024 ||
            ciphertextLength != encrypted.LongLength - HeaderBytes - TagBytes)
        {
            throw new InvalidDataException("The encrypted backup header is invalid.");
        }
        byte[] salt = header.Slice(16, SaltBytes).ToArray();
        byte[] nonce = header.Slice(16 + SaltBytes, NonceBytes).ToArray();
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
        byte[] plaintext = new byte[ciphertextLength];
        try
        {
            using AesGcm aes = new(key, TagBytes);
            aes.Decrypt(
                nonce,
                encrypted.AsSpan(HeaderBytes, (int)ciphertextLength),
                encrypted.AsSpan(HeaderBytes + (int)ciphertextLength, TagBytes),
                plaintext,
                header);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException(
                "The encrypted backup could not be authenticated with the supplied passphrase.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static byte[] BuildHeader(byte[] salt, byte[] nonce, long ciphertextLength)
    {
        byte[] header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), Pbkdf2Iterations);
        salt.CopyTo(header, 16);
        nonce.CopyTo(header, 16 + SaltBytes);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(HeaderBytes - 8, 8),
            ciphertextLength);
        return header;
    }

    private static void ValidatePayload(InstallationBackupPayload payload)
    {
        if (payload.SchemaVersion != SchemaVersion ||
            !IsBackupId(payload.BackupId) ||
            payload.CreatedAt < DateTimeOffset.UnixEpoch ||
            payload.SetupSchemaVersion <= 0 || payload.SetupRevision <= 0 ||
            !Enum.IsDefined(payload.Topology) ||
            !IsReleaseIdentity(payload.CurrentReleaseIdentity) ||
            (payload.RollbackReleaseIdentityKnown &&
             !IsReleaseIdentity(payload.RollbackReleaseIdentity)) ||
            (!payload.RollbackReleaseIdentityKnown &&
             !string.IsNullOrEmpty(payload.RollbackReleaseIdentity)) ||
            payload.Roots.Count is < 2 or > 4 ||
            payload.Roots.Select(root => root.Role).Distinct().Count() !=
                payload.Roots.Count ||
            payload.ExternalDependencies.Count > 16)
        {
            throw new InvalidDataException("The encrypted backup manifest is invalid.");
        }
        int count = 0;
        long total = 0;
        foreach (InstallationBackupRoot root in payload.Roots)
        {
            if (!Enum.IsDefined(root.Role) || !root.Present ||
                root.Files.Count > MaximumEntryCount ||
                root.Directories.Count > MaximumEntryCount)
            {
                throw new InvalidDataException("The encrypted backup root is invalid.");
            }
            HashSet<string> directoryNames = new(StringComparer.Ordinal);
            foreach (InstallationBackupDirectory directory in root.Directories)
            {
                if (!string.IsNullOrEmpty(directory.RelativePath))
                {
                    ValidateRelativePath(directory.RelativePath);
                }
                if (!directoryNames.Add(directory.RelativePath) ||
                    !ValidOwner(directory.Owner) ||
                    !ValidUnixMode(directory.UnixMode, directory: true))
                {
                    throw new InvalidDataException(
                        "The encrypted backup directory entry is invalid.");
                }
            }
            if (!directoryNames.Contains(string.Empty))
            {
                throw new InvalidDataException(
                    "The encrypted backup root directory metadata is missing.");
            }
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (InstallationBackupFile file in root.Files)
            {
                ValidateRelativePath(file.RelativePath);
                if (!names.Add(file.RelativePath) ||
                    file.Length < 0 || file.Length > MaximumFileBytes ||
                    !ValidOwner(file.Owner) ||
                    !ValidUnixMode(file.UnixMode, directory: false) ||
                    file.Sha256.Length != 64 ||
                    file.Sha256.Any(character =>
                        character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                {
                    throw new InvalidDataException("The encrypted backup file entry is invalid.");
                }
                byte[] content;
                try
                {
                    content = Convert.FromBase64String(file.ContentBase64);
                }
                catch (FormatException exception)
                {
                    throw new InvalidDataException(
                        "The encrypted backup file content is invalid.",
                        exception);
                }
                try
                {
                    if (content.LongLength != file.Length ||
                        !string.Equals(
                            Convert.ToHexStringLower(SHA256.HashData(content)),
                            file.Sha256,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The encrypted backup file digest is invalid.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
                count++;
                total += file.Length;
                if (count > MaximumEntryCount || total > MaximumPlaintextBytes)
                {
                    throw new InvalidDataException(
                        "The encrypted backup payload exceeds supported bounds.");
                }
            }
        }
        if (payload.ManagedProxyConfiguration is not null)
        {
            ValidateExactFile(payload.ManagedProxyConfiguration);
        }
    }

    private void ValidateRestoreReleaseCompatibility(InstallationBackupPayload payload)
    {
        string target = Path.Combine(m_paths.ReleaseDirectory, payload.CurrentReleaseIdentity);
        if (!Directory.Exists(target) ||
            (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Restore requires the backup's exact signed current release to be installed first.");
        }
        if (payload.RollbackReleaseIdentityKnown)
        {
            string rollback = Path.Combine(
                m_paths.ReleaseDirectory,
                payload.RollbackReleaseIdentity);
            if (!Directory.Exists(rollback) ||
                (File.GetAttributes(rollback) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Restore requires the backup's exact signed rollback release to be installed first.");
            }
        }
    }

    private async Task<RestoreRootTransaction> StageRootAsync(
        InstallationBackupRoot root,
        string target,
        string transactionId,
        CancellationToken cancellationToken)
    {
        string parent = Path.GetDirectoryName(target) ??
            throw new InvalidOperationException("Restore target has no parent directory.");
        Directory.CreateDirectory(parent);
        string name = Path.GetFileName(target);
        string staging = Path.Combine(parent, $".{name}.restore-{transactionId}");
        string previous = Path.Combine(parent, $".{name}.previous-{transactionId}");
        if (Directory.Exists(staging) || File.Exists(staging) ||
            Directory.Exists(previous) || File.Exists(previous))
        {
            throw new InvalidOperationException(
                "A prior restore staging or rollback path requires reconciliation.");
        }
        Directory.CreateDirectory(staging);
        InstallationBackupDirectory rootMetadata = root.Directories.Single(directory =>
            string.IsNullOrEmpty(directory.RelativePath));
        ApplyDirectoryMetadata(staging, rootMetadata);
        foreach (InstallationBackupDirectory directory in root.Directories
            .Where(item => !string.IsNullOrEmpty(item.RelativePath))
            .OrderBy(item => item.RelativePath.Count(character => character == '/'))
            .ThenBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            string directoryPath = SafeCombine(staging, directory.RelativePath);
            Directory.CreateDirectory(directoryPath);
            ApplyDirectoryMetadata(directoryPath, directory);
        }
        foreach (InstallationBackupFile file in root.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = SafeCombine(staging, file.RelativePath);
            string? directory = Path.GetDirectoryName(destination);
            if (directory is null)
            {
                throw new InvalidDataException("A restore entry has no parent directory.");
            }
            Directory.CreateDirectory(directory);
            byte[] content = Convert.FromBase64String(file.ContentBase64);
            try
            {
                await WriteNewFileAsync(
                    destination,
                    content,
                    file.UnixMode,
                    file.Owner,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
        if (root.Role == InstallationBackupRootRole.State)
        {
            await RemapStagedSetupPathsAsync(
                root,
                staging,
                cancellationToken);
        }
        return new RestoreRootTransaction(
            root.Role,
            target,
            staging,
            previous,
            applied: false);
    }

    private async Task RemapStagedSetupPathsAsync(
        InstallationBackupRoot root,
        string staging,
        CancellationToken cancellationToken)
    {
        string relative = Path.GetRelativePath(
            m_paths.StateDirectory,
            m_paths.SetupStatePath).Replace(Path.DirectorySeparatorChar, '/');
        InstallationBackupFile metadata = root.Files.SingleOrDefault(file =>
            string.Equals(file.RelativePath, relative, StringComparison.Ordinal)) ??
            throw new InvalidDataException(
                "The encrypted backup does not contain installation setup state.");
        string setupPath = SafeCombine(staging, relative);
        InstallationSetupState? state = JsonSerializer.Deserialize<InstallationSetupState>(
            await File.ReadAllBytesAsync(setupPath, cancellationToken),
            JsonOptions);
        if (state is null)
        {
            throw new InvalidDataException(
                "The restored installation setup state is empty.");
        }
        InstallationSetupStateValidator.Validate(state);
        InstallationSetupState remapped = state with { Paths = m_paths };
        InstallationSetupStateValidator.Validate(remapped);
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(remapped, JsonOptions);
        try
        {
            await using FileStream stream = new(
                setupPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(serialized, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
        if (OperatingSystem.IsLinux())
        {
            if (metadata.UnixMode is not null)
            {
                File.SetUnixFileMode(setupPath, (UnixFileMode)metadata.UnixMode.Value);
            }
            ApplyOwner(setupPath, metadata.Owner);
        }
    }

    private async Task<RestoreExactFileTransaction> StageExactFileAsync(
        InstallationBackupExactFile file,
        string target,
        string transactionId,
        CancellationToken cancellationToken)
    {
        string parent = Path.GetDirectoryName(target) ??
            throw new InvalidOperationException("Restore file target has no parent directory.");
        Directory.CreateDirectory(parent);
        string name = Path.GetFileName(target);
        string staging = Path.Combine(parent, $".{name}.restore-{transactionId}");
        string previous = Path.Combine(parent, $".{name}.previous-{transactionId}");
        if (File.Exists(staging) || File.Exists(previous) ||
            Directory.Exists(staging) || Directory.Exists(previous))
        {
            throw new InvalidOperationException(
                "A prior restore file staging path requires reconciliation.");
        }
        byte[] content = Convert.FromBase64String(file.ContentBase64);
        try
        {
            await WriteNewFileAsync(
                staging,
                content,
                file.UnixMode,
                InstallationBackupOwner.Root,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
        return new RestoreExactFileTransaction(target, staging, previous, applied: false);
    }

    private static void ApplyRootTransaction(RestoreRootTransaction transaction)
    {
        if (Directory.Exists(transaction.Target) || File.Exists(transaction.Target))
        {
            Directory.Move(transaction.Target, transaction.Previous);
        }
        Directory.Move(transaction.Staging, transaction.Target);
        transaction.Applied = true;
    }

    private static void ApplyExactFileTransaction(RestoreExactFileTransaction transaction)
    {
        if (File.Exists(transaction.Target))
        {
            File.Move(transaction.Target, transaction.Previous);
        }
        File.Move(transaction.Staging, transaction.Target);
        transaction.Applied = true;
    }

    private static void CompleteRootTransaction(RestoreRootTransaction transaction)
    {
        if (Directory.Exists(transaction.Previous))
        {
            Directory.Delete(transaction.Previous, recursive: true);
        }
    }

    private static void CompleteExactFileTransaction(RestoreExactFileTransaction transaction)
    {
        if (File.Exists(transaction.Previous))
        {
            File.Delete(transaction.Previous);
        }
    }

    private static void TryRollbackRoot(RestoreRootTransaction transaction)
    {
        if (Directory.Exists(transaction.Previous))
        {
            if (Directory.Exists(transaction.Target))
            {
                Directory.Delete(transaction.Target, recursive: true);
            }
            else if (File.Exists(transaction.Target))
            {
                File.Delete(transaction.Target);
            }
            Directory.Move(transaction.Previous, transaction.Target);
        }
        if (Directory.Exists(transaction.Staging))
        {
            Directory.Delete(transaction.Staging, recursive: true);
        }
    }

    private static void TryRollbackExactFile(RestoreExactFileTransaction transaction)
    {
        if (File.Exists(transaction.Previous))
        {
            if (File.Exists(transaction.Target))
            {
                File.Delete(transaction.Target);
            }
            File.Move(transaction.Previous, transaction.Target);
        }
        if (File.Exists(transaction.Staging))
        {
            File.Delete(transaction.Staging);
        }
    }

    private RestorePointerTransaction CreatePointerTransaction(
        string releaseIdentity,
        string transactionId)
    {
        string releaseRoot = Path.GetFullPath(m_paths.ReleaseDirectory);
        string parent = Path.GetDirectoryName(releaseRoot) ??
            throw new InvalidOperationException("Release root has no parent directory.");
        string current = Path.Combine(parent, "current");
        string target = Path.Combine(releaseRoot, releaseIdentity);
        string temporary = Path.Combine(parent, $".current.restore-{transactionId}");
        string displaced = Path.Combine(parent, $".current.previous-{transactionId}");
        if (PathEntryExists(temporary) || PathEntryExists(displaced))
        {
            throw new InvalidOperationException(
                "A prior current-pointer restore requires reconciliation.");
        }
        return new RestorePointerTransaction(
            current,
            target,
            temporary,
            displaced);
    }

    private static void ApplyCurrentPointer(RestorePointerTransaction transaction)
    {
        Directory.CreateSymbolicLink(transaction.Staging, transaction.Target);
        DirectoryInfo existing = new(transaction.Current);
        if (existing.LinkTarget is not null)
        {
            Directory.Move(transaction.Current, transaction.Previous);
        }
        else if (Directory.Exists(transaction.Current) || File.Exists(transaction.Current))
        {
            throw new InvalidOperationException(
                "The current release path is not a symbolic link.");
        }
        Directory.Move(transaction.Staging, transaction.Current);
        string? active = new DirectoryInfo(transaction.Current).LinkTarget;
        if (active is null ||
            !string.Equals(
                Path.GetFullPath(active, Path.GetDirectoryName(transaction.Current)!),
                Path.GetFullPath(transaction.Target),
                PathComparison))
        {
            throw new InvalidOperationException(
                "The restored current pointer did not reach the expected exact release.");
        }
    }

    private static void TryRollbackPointer(RestorePointerTransaction transaction)
    {
        if (new DirectoryInfo(transaction.Previous).LinkTarget is not null)
        {
            if (new DirectoryInfo(transaction.Current).LinkTarget is not null)
            {
                Directory.Delete(transaction.Current);
            }
            else if (Directory.Exists(transaction.Current) || File.Exists(transaction.Current))
            {
                throw new InvalidOperationException(
                    "The current release path changed to an unsafe non-link during restore recovery.");
            }
            Directory.Move(transaction.Previous, transaction.Current);
        }
        else if (new DirectoryInfo(transaction.Current).LinkTarget is not null)
        {
            string? currentTarget = new DirectoryInfo(transaction.Current).LinkTarget;
            string parent = Path.GetDirectoryName(transaction.Current)!;
            if (currentTarget is not null &&
                string.Equals(
                    Path.GetFullPath(currentTarget, parent),
                    Path.GetFullPath(transaction.Target),
                    PathComparison))
            {
                Directory.Delete(transaction.Current);
            }
        }
        if (new DirectoryInfo(transaction.Staging).LinkTarget is not null)
        {
            Directory.Delete(transaction.Staging);
        }
    }

    private static void CompletePointerTransaction(RestorePointerTransaction transaction)
    {
        if (new DirectoryInfo(transaction.Previous).LinkTarget is not null)
        {
            Directory.Delete(transaction.Previous);
        }
        if (new DirectoryInfo(transaction.Staging).LinkTarget is not null)
        {
            Directory.Delete(transaction.Staging);
        }
    }

    private RestoreJournalRoot ToJournalRoot(RestoreRootTransaction transaction) =>
        new(
            transaction.Role,
            transaction.Target,
            transaction.Staging,
            transaction.Previous);

    private static RestoreJournalFile ToJournalFile(
        RestoreExactFileTransaction transaction) =>
        new(transaction.Target, transaction.Staging, transaction.Previous);

    private string RestoreJournalPath =>
        Path.Combine(m_paths.BackupDirectory, RestoreJournalFileName);

    private void WriteRestoreJournal(RestoreJournal journal)
    {
        ValidateRestoreJournal(journal);
        string path = RestoreJournalPath;
        string temporary = path + ".new";
        if (File.Exists(temporary) || Directory.Exists(temporary))
        {
            throw new InvalidOperationException(
                "A restore journal staging file already exists and requires reconciliation.");
        }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        try
        {
            using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void RecoverPendingRestore()
    {
        string path = RestoreJournalPath;
        if (!File.Exists(path))
        {
            return;
        }
        if (!IsSafeRegularFile(path))
        {
            throw new InvalidDataException(
                "The pending restore journal is unsafe.");
        }
        RestoreJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<RestoreJournal>(
                File.ReadAllBytes(path),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The pending restore journal is malformed.",
                exception);
        }
        if (journal is null)
        {
            throw new InvalidDataException(
                "The pending restore journal is empty.");
        }
        ValidateRestoreJournal(journal);
        if (journal.Phase == RestoreJournalPhase.Prepared)
        {
            TryRollbackPointer(journal.Pointer);
            if (journal.Proxy is not null)
            {
                TryRollbackExactFile(new RestoreExactFileTransaction(
                    journal.Proxy.Target,
                    journal.Proxy.Staging,
                    journal.Proxy.Previous,
                    applied: PathEntryExists(journal.Proxy.Target)));
            }
            for (int index = journal.Roots.Count - 1; index >= 0; index--)
            {
                RestoreJournalRoot root = journal.Roots[index];
                TryRollbackRoot(new RestoreRootTransaction(
                    root.Role,
                    root.Target,
                    root.Staging,
                    root.Previous,
                    applied: PathEntryExists(root.Target)));
            }
        }
        else
        {
            foreach (RestoreJournalRoot root in journal.Roots)
            {
                CompleteRootTransaction(new RestoreRootTransaction(
                    root.Role,
                    root.Target,
                    root.Staging,
                    root.Previous,
                    applied: true));
                if (Directory.Exists(root.Staging))
                {
                    Directory.Delete(root.Staging, recursive: true);
                }
            }
            if (journal.Proxy is not null)
            {
                CompleteExactFileTransaction(new RestoreExactFileTransaction(
                    journal.Proxy.Target,
                    journal.Proxy.Staging,
                    journal.Proxy.Previous,
                    applied: true));
                if (File.Exists(journal.Proxy.Staging))
                {
                    File.Delete(journal.Proxy.Staging);
                }
            }
            CompletePointerTransaction(journal.Pointer);
        }
        DeleteRestoreJournal();
    }

    private void ValidateRestoreJournal(RestoreJournal journal)
    {
        if (journal.SchemaVersion != 1 || !IsBackupId(journal.BackupId) ||
            !Enum.IsDefined(journal.Phase) || journal.Roots.Count is < 2 or > 4 ||
            journal.Roots.Select(root => root.Role).Distinct().Count() !=
                journal.Roots.Count)
        {
            throw new InvalidDataException("The pending restore journal is invalid.");
        }
        foreach (RestoreJournalRoot root in journal.Roots)
        {
            string expectedTarget = TargetForRole(root.Role);
            string parent = Path.GetDirectoryName(expectedTarget) ??
                throw new InvalidDataException("A restore target has no parent.");
            string name = Path.GetFileName(expectedTarget);
            string expectedStaging = Path.Combine(
                parent,
                $".{name}.restore-{journal.BackupId}");
            string expectedPrevious = Path.Combine(
                parent,
                $".{name}.previous-{journal.BackupId}");
            if (!PathEquals(root.Target, expectedTarget) ||
                !PathEquals(root.Staging, expectedStaging) ||
                !PathEquals(root.Previous, expectedPrevious))
            {
                throw new InvalidDataException(
                    "The pending restore journal contains an unexpected root path.");
            }
        }
        if (journal.Proxy is not null)
        {
            string expectedTarget = ManagedCaddyConfigurationPath();
            string parent = Path.GetDirectoryName(expectedTarget) ??
                throw new InvalidDataException("The proxy restore target has no parent.");
            string name = Path.GetFileName(expectedTarget);
            if (!PathEquals(journal.Proxy.Target, expectedTarget) ||
                !PathEquals(
                    journal.Proxy.Staging,
                    Path.Combine(parent, $".{name}.restore-{journal.BackupId}")) ||
                !PathEquals(
                    journal.Proxy.Previous,
                    Path.Combine(parent, $".{name}.previous-{journal.BackupId}")))
            {
                throw new InvalidDataException(
                    "The pending restore journal contains an unexpected proxy path.");
            }
        }
        RestorePointerTransaction expectedPointer =
            CreatePointerTransactionForValidation(
                journal.Pointer.Target,
                journal.BackupId);
        if (!PathEquals(journal.Pointer.Current, expectedPointer.Current) ||
            !PathEquals(journal.Pointer.Target, expectedPointer.Target) ||
            !PathEquals(journal.Pointer.Staging, expectedPointer.Staging) ||
            !PathEquals(journal.Pointer.Previous, expectedPointer.Previous))
        {
            throw new InvalidDataException(
                "The pending restore journal contains an unexpected release pointer path.");
        }
    }

    private RestorePointerTransaction CreatePointerTransactionForValidation(
        string target,
        string transactionId)
    {
        string releaseRoot = Path.GetFullPath(m_paths.ReleaseDirectory);
        string normalizedTarget = Path.GetFullPath(target);
        string prefix = Path.TrimEndingDirectorySeparator(releaseRoot) +
            Path.DirectorySeparatorChar;
        if (!normalizedTarget.StartsWith(prefix, PathComparison) ||
            !IsReleaseIdentity(Path.GetFileName(normalizedTarget)))
        {
            throw new InvalidDataException(
                "The pending restore journal target release is invalid.");
        }
        string parent = Path.GetDirectoryName(releaseRoot) ??
            throw new InvalidDataException("The release root has no parent.");
        return new RestorePointerTransaction(
            Path.Combine(parent, "current"),
            normalizedTarget,
            Path.Combine(parent, $".current.restore-{transactionId}"),
            Path.Combine(parent, $".current.previous-{transactionId}"));
    }

    private void DeleteRestoreJournal()
    {
        if (File.Exists(RestoreJournalPath))
        {
            File.Delete(RestoreJournalPath);
        }
    }

    private static bool PathEntryExists(string path) =>
        File.Exists(path) || Directory.Exists(path) ||
        new DirectoryInfo(path).LinkTarget is not null;

    private string TargetForRole(InstallationBackupRootRole role) =>
        role switch
        {
            InstallationBackupRootRole.Configuration => m_paths.ConfigurationDirectory,
            InstallationBackupRootRole.State => m_paths.StateDirectory,
            InstallationBackupRootRole.Secrets => m_paths.SecretDirectory,
            InstallationBackupRootRole.ManagedProxyState => ManagedProxyStateDirectory(),
            _ => throw new InvalidDataException("The backup root role is unsupported.")
        };

    private (string Identity, bool Known) ReadRollbackIdentity()
    {
        string path = Path.Combine(
            m_paths.StateDirectory,
            "release-transactions",
            "active.json");
        if (!File.Exists(path) || !IsSafeRegularFile(path))
        {
            return (string.Empty, false);
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty(
                    "installedReleaseIdentity",
                    out JsonElement installed) ||
                installed.ValueKind != JsonValueKind.String)
            {
                return (string.Empty, false);
            }
            string value = installed.GetString() ?? string.Empty;
            return IsReleaseIdentity(value) ? (value, true) : (string.Empty, false);
        }
        catch (JsonException)
        {
            return (string.Empty, false);
        }
    }

    private DateTimeOffset? FindLatestBackupTimestamp()
    {
        if (!Directory.Exists(m_paths.BackupDirectory))
        {
            return null;
        }
        DateTimeOffset? latest = null;
        foreach (string file in Directory.EnumerateFiles(
            m_paths.BackupDirectory,
            $"*{BackupExtension}",
            SearchOption.TopDirectoryOnly))
        {
            if (!IsSafeRegularFile(file))
            {
                continue;
            }
            DateTimeOffset timestamp = File.GetLastWriteTimeUtc(file);
            if (latest is null || timestamp > latest)
            {
                latest = timestamp;
            }
        }
        return latest;
    }

    private bool EnsureBackupDirectory(bool readOnly)
    {
        try
        {
            if (!Directory.Exists(m_paths.BackupDirectory))
            {
                if (readOnly)
                {
                    return false;
                }
                Directory.CreateDirectory(m_paths.BackupDirectory);
            }
            ValidateDirectory(m_paths.BackupDirectory);
            if (OperatingSystem.IsLinux())
            {
                UnixFileMode mode = File.GetUnixFileMode(m_paths.BackupDirectory);
                if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> IncludedAuthorities(InstallationSetupState setup)
    {
        List<string> values =
        [
            "installation-configuration",
            "setup-state",
            "data-protection-keys",
            "radio-policies",
            "radio-onboarding-policy",
            "administrative-audit",
            "station-credentials",
            "release-signing-and-trust",
            "release-transaction-state",
            "managed-proxy-state",
            "current-and-rollback-release-identities"
        ];
        if (InstallationTopologyProfile.For(setup.Topology!.Value).GatewayRunsHere)
        {
            values.Add("local-users-passwords-mfa-and-sessions");
        }
        return values;
    }

    private static IReadOnlyList<string> DetermineExternalDependencies(
        InstallationSetupState? setup)
    {
        _ = setup;
        return
        [
            "DNS records and registrar/account access are external and are not embedded in the backup.",
            "Signed release package bytes are not embedded; install the recorded current and rollback releases before restore on a replacement host.",
            "Operator-managed reverse-proxy configuration and TLS private keys outside AetherSDR-owned paths require separate backup/restore.",
            "External identity-provider application registration, tenant/provider policy, and provider-side client-secret lifecycle remain external dependencies."
        ];
    }

    private static InstallationBackupSummary SummaryFrom(
        InstallationBackupPayload payload,
        long encryptedBytes)
    {
        int fileCount = payload.Roots.Sum(root => root.Files.Count) +
            (payload.ManagedProxyConfiguration is null ? 0 : 1);
        long plaintextBytes = payload.Roots.Sum(root => root.Files.Sum(file => file.Length)) +
            (payload.ManagedProxyConfiguration?.Length ?? 0);
        IReadOnlyList<string> authorities =
        [
            "installation-configuration",
            "durable-state",
            "credentials-and-trust",
            "managed-proxy-state",
            "current-and-rollback-release-identities"
        ];
        return new InstallationBackupSummary(
            SchemaVersion,
            payload.BackupId,
            payload.CreatedAt,
            payload.SetupRevision,
            payload.CurrentReleaseIdentity,
            payload.RollbackReleaseIdentity,
            payload.RollbackReleaseIdentityKnown,
            fileCount,
            plaintextBytes,
            encryptedBytes,
            authorities,
            payload.ExternalDependencies);
    }

    private static void ValidateExactFile(InstallationBackupExactFile file)
    {
        if (!Enum.IsDefined(file.Role) || file.Length < 0 ||
            file.Length > MaximumFileBytes || file.Sha256.Length != 64)
        {
            throw new InvalidDataException("The encrypted exact-file backup entry is invalid.");
        }
        byte[] content = Convert.FromBase64String(file.ContentBase64);
        try
        {
            if (content.LongLength != file.Length ||
                !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(content)),
                    file.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The encrypted exact-file backup digest is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<byte[]> ReadStableFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo before = new(path);
        if (!before.Exists || before.Length < 0 || before.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("A durable backup file is unavailable or too large.");
        }
        DateTime beforeWrite = before.LastWriteTimeUtc;
        byte[] content = await ReadBoundedFileAsync(path, MaximumFileBytes, cancellationToken);
        FileInfo after = new(path);
        if (!after.Exists || after.Length != before.Length ||
            after.LastWriteTimeUtc != beforeWrite)
        {
            CryptographicOperations.ZeroMemory(content);
            throw new IOException("A durable backup file changed while it was being read.");
        }
        return content;
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length < 0 || info.Length > maximumBytes ||
            info.Length > int.MaxValue)
        {
            throw new InvalidDataException("The input file exceeds the supported bound.");
        }
        byte[] content = new byte[info.Length];
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        int offset = 0;
        while (offset < content.Length)
        {
            int read = await stream.ReadAsync(
                content.AsMemory(offset),
                cancellationToken);
            if (read == 0)
            {
                CryptographicOperations.ZeroMemory(content);
                throw new EndOfStreamException("The input file ended unexpectedly.");
            }
            offset += read;
        }
        return content;
    }

    private static async Task WriteAtomicBackupAsync(
        string finalPath,
        byte[] encrypted,
        CancellationToken cancellationToken)
    {
        if (File.Exists(finalPath) || Directory.Exists(finalPath))
        {
            throw new IOException("The generated backup path already exists.");
        }
        string temporary = finalPath + ".new";
        if (File.Exists(temporary) || Directory.Exists(temporary))
        {
            throw new IOException("The backup staging path already exists.");
        }
        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temporary, finalPath);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            throw;
        }
    }

    private static async Task WriteNewFileAsync(
        string path,
        byte[] content,
        int? unixMode,
        InstallationBackupOwner owner,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        if (OperatingSystem.IsLinux())
        {
            UnixFileMode mode = unixMode is null
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : (UnixFileMode)unixMode.Value;
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                throw new InvalidDataException(
                    "A backup entry requests unsafe shared-write permissions.");
            }
            File.SetUnixFileMode(path, mode);
            ApplyOwner(path, owner);
        }
    }

    private static void ApplyDirectoryMetadata(
        string path,
        InstallationBackupDirectory directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        UnixFileMode mode = directory.UnixMode is null
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute
            : (UnixFileMode)directory.UnixMode.Value;
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidDataException(
                "A backup directory requests unsafe shared-write permissions.");
        }
        File.SetUnixFileMode(path, mode);
        ApplyOwner(path, directory.Owner);
    }

    private string ValidateBackupInputPath(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !Path.IsPathRooted(backupPath))
        {
            throw new InvalidOperationException("The backup path must be absolute.");
        }
        string canonical = Path.GetFullPath(backupPath);
        if (!canonical.EndsWith(BackupExtension, StringComparison.Ordinal) ||
            !IsSafeRegularFile(canonical))
        {
            throw new InvalidOperationException(
                "The backup input must be one regular encrypted AetherSDR backup file.");
        }
        return canonical;
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) ||
            passphrase.Length < MinimumPassphraseLength ||
            passphrase.Length > MaximumPassphraseLength ||
            passphrase.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Backup passphrase must contain {MinimumPassphraseLength}-{MaximumPassphraseLength} non-control characters.");
        }
    }

    private static void ValidateDirectory(string path)
    {
        DirectoryInfo directory = new(path);
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A durable backup directory is unsafe.");
        }
        if (OperatingSystem.IsLinux())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                throw new InvalidDataException(
                    "A durable backup directory is shared-writable.");
            }
        }
    }

    private static bool IsSafeDirectory(string path)
    {
        try
        {
            ValidateDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeRegularFile(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.LinkTarget is not null ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            if (OperatingSystem.IsLinux())
            {
                UnixFileMode mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.Contains('\0') || value.Split('/', '\\').Any(segment =>
                segment is "" or "." or ".."))
        {
            throw new InvalidDataException("A backup entry contains an unsafe relative path.");
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        ValidateRelativePath(relative);
        string normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        string combined = Path.GetFullPath(Path.Combine(root, normalized));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, PathComparison))
        {
            throw new InvalidDataException("A restore entry escaped its target root.");
        }
        return combined;
    }

    private static bool IsDescendant(string candidate, string parent)
    {
        string normalizedCandidate = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidate));
        string normalizedParent = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(parent));
        if (PathEquals(normalizedCandidate, normalizedParent))
        {
            return false;
        }
        return normalizedCandidate.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            PathComparison);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    private static InstallationBackupOwner LogicalOwner(
        InstallationBackupRootRole role,
        string relativePath,
        InstallationSetupState setup)
    {
        if (role == InstallationBackupRootRole.ManagedProxyState)
        {
            return InstallationBackupOwner.Root;
        }
        InstallationTopologyProfile profile =
            InstallationTopologyProfile.For(setup.Topology!.Value);
        string normalized = relativePath.Replace('\\', '/');
        if (normalized.Equals("aetherremote", StringComparison.Ordinal) ||
            normalized.StartsWith("aetherremote/", StringComparison.Ordinal))
        {
            return InstallationBackupOwner.AetherRemote;
        }
        return profile.GatewayRunsHere
            ? InstallationBackupOwner.AetherSdr
            : InstallationBackupOwner.AetherRemote;
    }

    private static bool ValidOwner(InstallationBackupOwner owner) =>
        Enum.IsDefined(owner);

    private static bool ValidUnixMode(int? unixMode, bool directory)
    {
        if (unixMode is null || !OperatingSystem.IsLinux())
        {
            return true;
        }
        UnixFileMode mode = (UnixFileMode)unixMode.Value;
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            return false;
        }
        return !directory || (mode & UnixFileMode.UserExecute) != 0;
    }

    private static void ApplyOwner(string path, InstallationBackupOwner owner)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }
        string name = owner switch
        {
            InstallationBackupOwner.Root => "root",
            InstallationBackupOwner.AetherSdr => "aethersdr",
            InstallationBackupOwner.AetherRemote => "aetherremote",
            _ => throw new InvalidDataException("The backup owner role is invalid.")
        };
        (uint uid, uint gid) = ResolveUnixAccount(name);
        if (Chown(path, uid, gid) != 0)
        {
            throw new IOException(
                $"The restored path owner could not be mapped to service account '{name}'.");
        }
    }

    private static (uint Uid, uint Gid) ResolveUnixAccount(string name)
    {
        string? passwd = File.ReadLines("/etc/passwd").FirstOrDefault(line =>
            line.StartsWith(name + ":", StringComparison.Ordinal));
        if (passwd is null)
        {
            throw new InvalidOperationException(
                $"Required restore service account '{name}' does not exist.");
        }
        string[] fields = passwd.Split(':');
        if (fields.Length < 4 ||
            !uint.TryParse(fields[2], out uint uid) ||
            !uint.TryParse(fields[3], out uint gid))
        {
            throw new InvalidDataException(
                $"Required restore service account '{name}' has invalid local identity metadata.");
        }
        return (uid, gid);
    }

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    private static bool IsBackupId(string value) =>
        value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsReleaseIdentity(string value)
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

    private string ManagedProxyStateDirectory() =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(m_paths.StateDirectory),
            "/var/lib/aethersdr",
            StringComparison.Ordinal)
            ? "/var/lib/aethersdr-installer/proxy"
            : Path.Combine(m_paths.StateDirectory, "installer-proxy");

    private string ManagedCaddyMarkerPath() =>
        Path.Combine(ManagedProxyStateDirectory(), "managed-caddy.sha256");

    private string ManagedCaddyConfigurationPath() =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(m_paths.ConfigurationDirectory),
            "/etc/aethersdr",
            StringComparison.Ordinal)
            ? "/etc/caddy/Caddyfile"
            : Path.Combine(m_paths.ConfigurationDirectory, "managed-caddy", "Caddyfile");

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private enum InstallationBackupRootRole
    {
        Configuration = 1,
        State = 2,
        Secrets = 3,
        ManagedProxyState = 4
    }

    private enum InstallationBackupExactFileRole
    {
        ManagedProxyConfiguration = 1
    }

    private enum InstallationBackupOwner
    {
        Root = 1,
        AetherSdr = 2,
        AetherRemote = 3
    }

    private sealed record InstallationBackupPayload(
        int SchemaVersion,
        string BackupId,
        DateTimeOffset CreatedAt,
        int SetupSchemaVersion,
        long SetupRevision,
        InstallationTopologyKind Topology,
        string CurrentReleaseIdentity,
        string RollbackReleaseIdentity,
        bool RollbackReleaseIdentityKnown,
        IReadOnlyList<InstallationBackupRoot> Roots,
        InstallationBackupExactFile? ManagedProxyConfiguration,
        IReadOnlyList<string> ExternalDependencies);

    private sealed record InstallationBackupRoot(
        InstallationBackupRootRole Role,
        bool Present,
        IReadOnlyList<InstallationBackupDirectory> Directories,
        IReadOnlyList<InstallationBackupFile> Files);

    private sealed record InstallationBackupDirectory(
        string RelativePath,
        int? UnixMode,
        InstallationBackupOwner Owner);

    private sealed record InstallationBackupFile(
        string RelativePath,
        long Length,
        string Sha256,
        int? UnixMode,
        InstallationBackupOwner Owner,
        string ContentBase64);

    private sealed record InstallationBackupExactFile(
        InstallationBackupExactFileRole Role,
        long Length,
        string Sha256,
        int? UnixMode,
        string ContentBase64);

    private sealed class RestoreRootTransaction(
        InstallationBackupRootRole role,
        string target,
        string staging,
        string previous,
        bool applied)
    {
        internal InstallationBackupRootRole Role { get; } = role;
        internal string Target { get; } = target;
        internal string Staging { get; } = staging;
        internal string Previous { get; } = previous;
        internal bool Applied { get; set; } = applied;
    }

    private sealed class RestoreExactFileTransaction(
        string target,
        string staging,
        string previous,
        bool applied)
    {
        internal string Target { get; } = target;
        internal string Staging { get; } = staging;
        internal string Previous { get; } = previous;
        internal bool Applied { get; set; } = applied;
    }

    private enum RestoreJournalPhase
    {
        Prepared = 1,
        Committed = 2
    }

    private sealed record RestoreJournal(
        int SchemaVersion,
        string BackupId,
        RestoreJournalPhase Phase,
        IReadOnlyList<RestoreJournalRoot> Roots,
        RestoreJournalFile? Proxy,
        RestorePointerTransaction Pointer);

    private sealed record RestoreJournalRoot(
        InstallationBackupRootRole Role,
        string Target,
        string Staging,
        string Previous);

    private sealed record RestoreJournalFile(
        string Target,
        string Staging,
        string Previous);

    private sealed record RestorePointerTransaction(
        string Current,
        string Target,
        string Staging,
        string Previous);
}
