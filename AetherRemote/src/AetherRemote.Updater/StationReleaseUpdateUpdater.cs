using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AetherRemote.Protocol;

[SupportedOSPlatform("linux")]
internal sealed partial class StationReleaseUpdateUpdater(
    ILogger<StationReleaseUpdateUpdater> logger,
    IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    internal const string RuntimeDirectory = "/run/aetherremote-release-updater";
    internal const string SocketPath =
        "/run/aetherremote-release-updater/release.sock";
    internal const int MaximumMessageBytes = 16 * 1024;

    private const string StagingRoot = "/var/lib/aetherremote/release-staging";
    private const string ReleaseRoot = "/opt/aetherremote/releases";
    private const string AgentLink = "/opt/aetherremote/agent";
    private const string EngineLink = "/opt/aetherremote/station-engine";
    private const string UpdaterLink = "/opt/aetherremote/updater";
    private const string TrustKeyPath =
        "/etc/aetherremote/release-trust/release-public-key.der";
    private const string TrustKeyShaPath =
        "/etc/aetherremote/release-trust/release-public-key.sha256";
    private const string TransactionPath =
        "/var/lib/aetherremote/release-update-state.json";
    private const string CompletionPath =
        "/var/lib/aetherremote/release-update-completion.json";
    private const string SystemctlPath = "/usr/bin/systemctl";
    private const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private static readonly TimeSpan ConfirmationDeadline =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SystemctlTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim m_transactionGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The AetherRemote station updater requires Linux.");
        }
        ValidateFixedLayout();
        Directory.CreateDirectory(RuntimeDirectory);
        File.SetUnixFileMode(
            RuntimeDirectory,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute);
        RemoveStaleSocket(SocketPath);

        using Socket listener = new(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        File.SetUnixFileMode(
            SocketPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite);
        listener.Listen(backlog: 4);
        logger.LogInformation(
            "AetherRemote signed release updater is listening on its fixed local socket");

        Task watchdog = RunRollbackWatchdogAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket connection = await listener.AcceptAsync(stoppingToken);
                await HandleAsync(connection, stoppingToken);
            }
        }
        finally
        {
            listener.Close();
            RemoveStaleSocket(SocketPath);
            try
            {
                await watchdog;
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task HandleAsync(
        Socket connection,
        CancellationToken cancellationToken)
    {
        using (connection)
        await using (NetworkStream stream = new(connection, ownsSocket: false))
        {
            LocalStationReleaseUpdateResult result;
            try
            {
                LocalStationReleaseUpdateRequest request =
                    await ReadAsync<LocalStationReleaseUpdateRequest>(
                        stream,
                        cancellationToken);
                string? error =
                    StationProtocolValidator.ValidateLocalReleaseUpdateRequest(
                        request);
                if (error is not null)
                {
                    throw new InvalidDataException(error);
                }
                await m_transactionGate.WaitAsync(cancellationToken);
                try
                {
                    result = await ExecuteRequestAsync(
                        request,
                        cancellationToken);
                }
                finally
                {
                    m_transactionGate.Release();
                }
            }
            catch (Exception exception)
                when (exception is JsonException or IOException or
                      InvalidDataException or InvalidOperationException or
                      CryptographicException or UnauthorizedAccessException or
                      System.Security.SecurityException or NotSupportedException or
                      ArgumentException)
            {
                logger.LogWarning(
                    exception,
                    "Rejected or failed a local signed release-update request");
                return;
            }
            await WriteAsync(stream, result, cancellationToken);
            if (string.Equals(
                    result.Action,
                    StationLocalUpdaterActions.Acknowledge,
                    StringComparison.Ordinal) &&
                result.Succeeded &&
                string.Equals(
                    result.Outcome,
                    "acknowledged",
                    StringComparison.Ordinal) &&
                !result.RolledBack &&
                !string.IsNullOrEmpty(result.CompletedReleaseIdentity))
            {
                // The Agent has already reported the durable completion to the
                // broker and acknowledged it locally. Exit nonzero so
                // Restart=on-failure reloads this fixed-purpose service from
                // the newly switched updater symlink.
                Environment.ExitCode = 75;
                applicationLifetime.StopApplication();
            }
        }
    }

    private async Task<LocalStationReleaseUpdateResult> ExecuteRequestAsync(
        LocalStationReleaseUpdateRequest request,
        CancellationToken cancellationToken)
    {
        return request.Action switch
        {
            StationLocalUpdaterActions.Apply =>
                await ApplyAsync(request, cancellationToken),
            StationLocalUpdaterActions.Rollback =>
                await RollbackAsync(request, cancellationToken),
            StationLocalUpdaterActions.Confirm =>
                await ConfirmAsync(request, cancellationToken),
            StationLocalUpdaterActions.Acknowledge =>
                await AcknowledgeAsync(request, cancellationToken),
            _ => throw new InvalidDataException(
                "The local station updater action is unsupported.")
        };
    }

    private async Task<LocalStationReleaseUpdateResult> ApplyAsync(
        LocalStationReleaseUpdateRequest request,
        CancellationToken cancellationToken)
    {
        UpdateTransaction? existing = ReadTransaction();
        if (existing is not null)
        {
            throw new InvalidOperationException(
                "A prior station release update requires confirmation or rollback.");
        }
        UpdateCompletion? priorCompletion = ReadCompletion();
        if (priorCompletion is not null)
        {
            if (!priorCompletion.Acknowledged)
            {
                throw new InvalidOperationException(
                    "A prior station release completion still requires broker acknowledgement.");
            }
            DeleteCompletion();
        }
        string previous = ReadActiveReleaseIdentity();
        if (string.Equals(
                previous,
                request.ReleaseIdentity,
                StringComparison.Ordinal))
        {
            return Result(
                request,
                true,
                "already-current",
                previous,
                previous,
                requiresRestart: false);
        }

        string staging = StagingDirectory(request.CorrelationId);
        VerifiedBundle bundle = VerifyStagedBundle(
            staging,
            request.ReleaseIdentity);
        string target = ReleaseDirectory(request.ReleaseIdentity);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new InvalidOperationException(
                "The target station release directory already exists and is not active.");
        }
        string extraction = Path.Combine(
            ReleaseRoot,
            $".{request.ReleaseIdentity}.{request.CorrelationId}.staging");
        if (Directory.Exists(extraction) || File.Exists(extraction))
        {
            throw new InvalidOperationException(
                "The target station release extraction path already exists.");
        }

        Directory.CreateDirectory(extraction);
        try
        {
            string agentTarget = Path.Combine(extraction, "agent");
            string engineTarget = Path.Combine(extraction, "station-engine");
            Directory.CreateDirectory(agentTarget);
            Directory.CreateDirectory(engineTarget);
            ExtractSafeArchive(bundle.AgentArchivePath, agentTarget);
            ExtractSafeArchive(bundle.EngineArchivePath, engineTarget);
            ValidateInstalledReleaseTree(agentTarget, engineTarget);
            HardenTree(extraction);
            Directory.Move(extraction, target);
        }
        catch
        {
            if (Directory.Exists(extraction))
            {
                Directory.Delete(extraction, recursive: true);
            }
            throw;
        }

        UpdateTransaction transaction = new(
            1,
            request.CorrelationId,
            previous,
            request.ReleaseIdentity,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(ConfirmationDeadline));
        WriteTransaction(transaction);
        try
        {
            SwitchReleaseLinks(request.ReleaseIdentity);
            InstallSignedUnits(request.ReleaseIdentity);
            await RunSystemctlAsync(
                ["daemon-reload"],
                cancellationToken);
            await RunSystemctlAsync(
                ["restart", "aetherremote-station-engine.service"],
                cancellationToken);
            return Result(
                request,
                true,
                "applied",
                request.ReleaseIdentity,
                previous,
                requiresRestart: true);
        }
        catch
        {
            try
            {
                await RollbackTransactionAsync(transaction, cancellationToken);
            }
            catch (Exception rollbackException)
            {
                logger.LogCritical(
                    rollbackException,
                    "Automatic rollback failed after release apply error; transaction remains pending");
            }
            throw;
        }
    }

    private async Task<LocalStationReleaseUpdateResult> RollbackAsync(
        LocalStationReleaseUpdateRequest request,
        CancellationToken cancellationToken)
    {
        UpdateTransaction? transaction = ReadTransaction();
        if (transaction is null ||
            !string.Equals(
                transaction.CorrelationId,
                request.CorrelationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                transaction.TargetReleaseIdentity,
                request.ReleaseIdentity,
                StringComparison.Ordinal))
        {
            string active = ReadActiveReleaseIdentity();
            return Result(
                request,
                false,
                "rollback-unavailable",
                active,
                active,
                requiresRestart: false);
        }
        await RollbackTransactionAsync(transaction, cancellationToken);
        return Result(
            request,
            true,
            "rolled-back",
            transaction.PreviousReleaseIdentity,
            transaction.PreviousReleaseIdentity,
            requiresRestart: false);
    }

    private Task<LocalStationReleaseUpdateResult> ConfirmAsync(
        LocalStationReleaseUpdateRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string active = ReadActiveReleaseIdentity();
        if (!string.Equals(active, request.ReleaseIdentity, StringComparison.Ordinal))
        {
            return Task.FromResult(
                Result(
                    request,
                    false,
                    "active-mismatch",
                    active,
                    active,
                    requiresRestart: false));
        }

        UpdateTransaction? transaction = ReadTransaction();
        if (transaction is not null)
        {
            if (!string.Equals(
                    transaction.TargetReleaseIdentity,
                    request.ReleaseIdentity,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(
                    Result(
                        request,
                        false,
                        "pending-mismatch",
                        active,
                        transaction.PreviousReleaseIdentity,
                        requiresRestart: false));
            }
            UpdateCompletion newCompletion = new(
                1,
                transaction.CorrelationId,
                transaction.TargetReleaseIdentity,
                active,
                "confirmed",
                RolledBack: false,
                DateTimeOffset.UtcNow);
            WriteCompletion(newCompletion);
            DeleteTransaction();
            TryDeleteStaging(transaction.CorrelationId);
            return Task.FromResult(
                CompletionResult(request, newCompletion));
        }

        UpdateCompletion? completion = ReadCompletion();
        if (completion is not null)
        {
            if (completion.Acknowledged)
            {
                DeleteCompletion();
                return Task.FromResult(
                    Result(
                        request,
                        true,
                        "current",
                        active,
                        active,
                        requiresRestart: false));
            }
            if (!string.Equals(
                    completion.ActiveReleaseIdentity,
                    active,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(
                    Result(
                        request,
                        false,
                        "completion-mismatch",
                        active,
                        active,
                        requiresRestart: false));
            }
            return Task.FromResult(
                CompletionResult(request, completion));
        }

        return Task.FromResult(
            Result(
                request,
                true,
                "current",
                active,
                active,
                requiresRestart: false));
    }

    private Task<LocalStationReleaseUpdateResult> AcknowledgeAsync(
        LocalStationReleaseUpdateRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateCompletion? completion = ReadCompletion();
        string active = ReadActiveReleaseIdentity();
        if (completion is null ||
            !string.Equals(
                completion.CorrelationId,
                request.CorrelationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                completion.ActiveReleaseIdentity,
                active,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.ReleaseIdentity,
                active,
                StringComparison.Ordinal))
        {
            return Task.FromResult(
                Result(
                    request,
                    false,
                    "acknowledge-unavailable",
                    active,
                    active,
                    requiresRestart: false));
        }

        bool newlyAcknowledged = !completion.Acknowledged;
        if (newlyAcknowledged)
        {
            completion = completion with { Acknowledged = true };
            ReplaceCompletion(completion);
        }
        return Task.FromResult(
            Result(
                request,
                true,
                newlyAcknowledged
                    ? "acknowledged"
                    : "already-acknowledged",
                active,
                active,
                requiresRestart: false,
                correlationId: completion.CorrelationId,
                completedReleaseIdentity:
                    completion.TargetReleaseIdentity,
                rolledBack: completion.RolledBack));
    }

    private static LocalStationReleaseUpdateResult CompletionResult(
        LocalStationReleaseUpdateRequest request,
        UpdateCompletion completion) =>
        Result(
            request,
            true,
            completion.Outcome,
            completion.ActiveReleaseIdentity,
            completion.ActiveReleaseIdentity,
            requiresRestart: false,
            correlationId: completion.CorrelationId,
            completedReleaseIdentity: completion.TargetReleaseIdentity,
            rolledBack: completion.RolledBack);

    private async Task RunRollbackWatchdogAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await m_transactionGate.WaitAsync(cancellationToken);
            try
            {
                UpdateTransaction? transaction = ReadTransaction();
                if (transaction is null ||
                    DateTimeOffset.UtcNow < transaction.ConfirmationDeadlineUtc)
                {
                    continue;
                }
                logger.LogWarning(
                    "Station release {ReleaseIdentity} did not confirm startup; rolling back to {PreviousReleaseIdentity}",
                    transaction.TargetReleaseIdentity,
                    transaction.PreviousReleaseIdentity);
                await RollbackTransactionAsync(
                    transaction,
                    cancellationToken,
                    persistCompletion: true);
                await RunSystemctlAsync(
                    ["restart", "aetherremote-agent.service"],
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is IOException or InvalidDataException or
                      InvalidOperationException or UnauthorizedAccessException or
                      System.ComponentModel.Win32Exception)
            {
                logger.LogCritical(
                    exception,
                    "The station release rollback watchdog could not restore the previous release");
            }
            finally
            {
                m_transactionGate.Release();
            }
        }
    }

    private async Task RollbackTransactionAsync(
        UpdateTransaction transaction,
        CancellationToken cancellationToken,
        bool persistCompletion = false)
    {
        SwitchReleaseLinks(transaction.PreviousReleaseIdentity);
        InstallSignedUnits(transaction.PreviousReleaseIdentity);
        await RunSystemctlAsync(["daemon-reload"], cancellationToken);
        await RunSystemctlAsync(
            ["restart", "aetherremote-station-engine.service"],
            cancellationToken);
        string target = ReleaseDirectory(transaction.TargetReleaseIdentity);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        if (persistCompletion)
        {
            WriteCompletion(
                new UpdateCompletion(
                    1,
                    transaction.CorrelationId,
                    transaction.TargetReleaseIdentity,
                    transaction.PreviousReleaseIdentity,
                    "startup-rollback",
                    RolledBack: true,
                    DateTimeOffset.UtcNow));
        }
        DeleteTransaction();
        TryDeleteStaging(transaction.CorrelationId);
    }

    private static VerifiedBundle VerifyStagedBundle(
        string staging,
        string expectedReleaseIdentity)
    {
        ValidatePrivateDirectory(staging);
        string manifestPath = ExactStagingFile(staging, "release-manifest.json");
        string agentPath = ExactStagingFile(staging, "agent.tar.gz");
        string enginePath = ExactStagingFile(staging, "station-engine.tar.gz");
        byte[] manifestBytes = File.ReadAllBytes(manifestPath);
        if (manifestBytes.Length is < 2 or > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                "The root updater rejected the staged manifest size.");
        }
        byte[] key = ReadPinnedKey();
        using JsonDocument document = JsonDocument.Parse(manifestBytes);
        EnsureNoDuplicateProperties(document.RootElement);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("payload", out JsonElement payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("signature", out JsonElement signature) ||
            signature.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The root updater rejected the staged manifest shape.");
        }
        string releaseIdentity = RequiredString(payload, "releaseIdentity", 96);
        string version = RequiredString(payload, "version", 96);
        string architecture = RequiredString(payload, "architecture", 32);
        (string ManifestArchitecture, string PackageArchitecture) expectedArchitecture =
            RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => ("linuxX64", "linux-x64"),
                Architecture.Arm64 => ("linuxArm64", "linux-arm64"),
                _ => throw new PlatformNotSupportedException(
                    "The root updater supports x64 and arm64 only.")
            };
        if (!string.Equals(
                releaseIdentity,
                expectedReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                architecture,
                expectedArchitecture.ManifestArchitecture,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The staged release identity or architecture is mismatched.");
        }
        if (!payload.TryGetProperty("txSupport", out JsonElement txSupport) ||
            !txSupport.TryGetProperty("enablesTransmit", out JsonElement enablesTx) ||
            enablesTx.ValueKind != JsonValueKind.False)
        {
            throw new InvalidDataException(
                "The root updater refuses a station release that declares transmit enabled.");
        }

        string algorithm = RequiredString(signature, "algorithm", 64);
        _ = RequiredString(signature, "keyId", 64);
        string signatureValue = RequiredString(signature, "value", 512);
        if (!string.Equals(
                algorithm,
                "ecdsaP256Sha256",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The root updater supports only ECDSA P-256 signed releases.");
        }
        byte[] signatureBytes = DecodeBase64Url(signatureValue);
        if (signatureBytes.Length != 64)
        {
            throw new InvalidDataException(
                "The root updater rejected the signature length.");
        }
        byte[] suffix = Encoding.UTF8.GetBytes(
            $",\"value\":\"{signatureValue}\"}}}}");
        if (!manifestBytes.AsSpan().EndsWith(suffix))
        {
            throw new InvalidDataException(
                "The root updater rejected a non-canonical manifest serialization.");
        }
        byte[] signingBytes = new byte[
            manifestBytes.Length - suffix.Length + 2];
        manifestBytes.AsSpan(0, manifestBytes.Length - suffix.Length)
            .CopyTo(signingBytes);
        signingBytes[^2] = (byte)'}';
        signingBytes[^1] = (byte)'}';
        using ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(key, out int bytesRead);
        if (bytesRead != key.Length ||
            !verifier.VerifyData(
                signingBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new InvalidDataException(
                "The root updater rejected the release signature.");
        }
        CryptographicOperations.ZeroMemory(signingBytes);

        if (!payload.TryGetProperty("packages", out JsonElement packages) ||
            packages.ValueKind != JsonValueKind.Array ||
            packages.GetArrayLength() != 4)
        {
            throw new InvalidDataException(
                "The root updater rejected the package inventory.");
        }
        Dictionary<string, SignedPackage> roles = new(StringComparer.Ordinal);
        foreach (JsonElement package in packages.EnumerateArray())
        {
            string packageIdentity = RequiredString(
                package,
                "packageIdentity",
                96);
            string role = RequiredString(package, "role", 64);
            string fileName = RequiredString(package, "fileName", 160);
            string digest = RequiredString(package, "sha256", 64).ToLowerInvariant();
            long length = RequiredInt64(package, "length");
            (string ExpectedIdentity, string ExpectedFileName)? expected =
                ExpectedPackageDeclaration(
                    role,
                    expectedArchitecture.PackageArchitecture);
            if (expected is null ||
                !string.Equals(
                    packageIdentity,
                    expected.Value.ExpectedIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    fileName,
                    expected.Value.ExpectedFileName,
                    StringComparison.Ordinal) ||
                !Sha256Pattern().IsMatch(digest) ||
                length is <= 0 or > MaximumPackageBytes ||
                !roles.TryAdd(role, new SignedPackage(fileName, digest, length)))
            {
                throw new InvalidDataException(
                    "The root updater rejected a package declaration.");
            }
        }
        SignedPackage agent = RequireRole(roles, "aetherRemoteAgent");
        SignedPackage engine = RequireRole(roles, "stationEngine");
        _ = RequireRole(roles, "gatewayWeb");
        _ = RequireRole(roles, "broker");
        VerifyPackage(agentPath, agent);
        VerifyPackage(enginePath, engine);
        return new VerifiedBundle(
            releaseIdentity,
            version,
            agentPath,
            enginePath);
    }

    private static void ExtractSafeArchive(string archivePath, string destination)
    {
        const int maximumEntries = 10_000;
        const long maximumExpandedBytes = 2L * 1024 * 1024 * 1024;
        string root = Path.GetFullPath(destination);
        int count = 0;
        long total = 0;
        using FileStream file = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using TarReader tar = new(gzip, leaveOpen: false);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry(copyData: false)) is not null)
        {
            count++;
            if (count > maximumEntries)
            {
                throw new InvalidDataException(
                    "The station release archive contains too many entries.");
            }
            if (entry.EntryType is not TarEntryType.Directory and
                not TarEntryType.RegularFile and
                not TarEntryType.V7RegularFile)
            {
                throw new InvalidDataException(
                    "The station release archive contains a link, device, or unsupported entry.");
            }
            string relative = NormalizeArchiveEntryName(
                entry.Name,
                entry.EntryType == TarEntryType.Directory);
            if (relative.Length == 0)
            {
                continue;
            }
            string target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The station release archive escaped its extraction root.");
            }
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                File.SetUnixFileMode(
                    target,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
                continue;
            }
            if (entry.Length is < 0 or > MaximumPackageBytes)
            {
                throw new InvalidDataException(
                    "The station release archive entry size is invalid.");
            }
            total = checked(total + entry.Length);
            if (total > maximumExpandedBytes || entry.DataStream is null)
            {
                throw new InvalidDataException(
                    "The station release archive expanded size is invalid.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using FileStream output = new(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan);
            entry.DataStream.CopyTo(output);
            output.Flush(flushToDisk: true);
            bool executable =
                (entry.Mode &
                 (UnixFileMode.UserExecute |
                  UnixFileMode.GroupExecute |
                  UnixFileMode.OtherExecute)) != 0;
            File.SetUnixFileMode(
                target,
                executable
                    ? UnixFileMode.UserRead |
                      UnixFileMode.UserWrite |
                      UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead |
                      UnixFileMode.GroupExecute |
                      UnixFileMode.OtherRead |
                      UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead |
                      UnixFileMode.UserWrite |
                      UnixFileMode.GroupRead |
                      UnixFileMode.OtherRead);
        }
    }

    private static void ValidateInstalledReleaseTree(
        string agentDirectory,
        string engineDirectory)
    {
        string[] requiredExecutables =
        [
            Path.Combine(agentDirectory, "AetherRemote.Agent"),
            Path.Combine(agentDirectory, "updater", "AetherRemote.Updater"),
            Path.Combine(engineDirectory, "AetherSDR.Web")
        ];
        string[] requiredFiles =
        [
            Path.Combine(agentDirectory, "aetherremote-agent.service"),
            Path.Combine(agentDirectory, "aetherremote-station-engine.service"),
            Path.Combine(agentDirectory, "aetherremote-release-updater.service"),
            Path.Combine(agentDirectory, "enroll-station.sh")
        ];
        foreach (string path in requiredExecutables)
        {
            ValidateRegularFile(path, 2L * 1024 * 1024 * 1024);
            File.SetUnixFileMode(
                path,
                File.GetUnixFileMode(path) |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute);
        }
        foreach (string path in requiredFiles)
        {
            ValidateRegularFile(path, 1024 * 1024);
        }
    }

    private static void HardenTree(string root)
    {
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
        foreach (string directory in Directory.EnumerateDirectories(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
        }
    }

    private static void SwitchReleaseLinks(string releaseIdentity)
    {
        string release = ReleaseDirectory(releaseIdentity);
        string agent = Path.Combine(release, "agent");
        string engine = Path.Combine(release, "station-engine");
        string updater = Path.Combine(agent, "updater");
        ValidateInstalledReleaseTree(agent, engine);
        ReplaceDirectorySymlink(AgentLink, agent);
        ReplaceDirectorySymlink(EngineLink, engine);
        ReplaceDirectorySymlink(UpdaterLink, updater);
    }

    private static void InstallSignedUnits(string releaseIdentity)
    {
        string agent = Path.Combine(
            ReleaseDirectory(releaseIdentity),
            "agent");
        CopyFixedUnit(
            Path.Combine(agent, "aetherremote-agent.service"),
            "/etc/systemd/system/aetherremote-agent.service");
        CopyFixedUnit(
            Path.Combine(agent, "aetherremote-station-engine.service"),
            "/etc/systemd/system/aetherremote-station-engine.service");
        CopyFixedUnit(
            Path.Combine(agent, "aetherremote-release-updater.service"),
            "/etc/systemd/system/aetherremote-release-updater.service");
    }

    private static void CopyFixedUnit(string source, string target)
    {
        ValidateRegularFile(source, 1024 * 1024);
        string temporary = target + ".aetherremote-new";
        if (File.Exists(temporary) || Directory.Exists(temporary))
        {
            throw new InvalidOperationException(
                "A station unit staging path already exists.");
        }
        File.Copy(source, temporary, overwrite: false);
        File.SetUnixFileMode(
            temporary,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead);
        File.Move(temporary, target, overwrite: true);
    }

    private static void ReplaceDirectorySymlink(string link, string target)
    {
        if (!Directory.Exists(target))
        {
            throw new InvalidOperationException(
                "A release symlink target is unavailable.");
        }
        string temporary = link + ".aetherremote-new";
        RemoveExpectedTemporaryLink(temporary);
        Directory.CreateSymbolicLink(temporary, target);
        File.Move(temporary, link, overwrite: true);
    }

    private static void RemoveExpectedTemporaryLink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        DirectoryInfo info = new(path);
        info.Refresh();
        if (info.LinkTarget is null)
        {
            throw new InvalidOperationException(
                "A release symlink staging path is occupied by a non-link.");
        }
        Directory.Delete(path);
    }

    private static string ReadActiveReleaseIdentity()
    {
        string agent = ReadLinkReleaseIdentity(AgentLink, "agent");
        string engine = ReadLinkReleaseIdentity(EngineLink, "station-engine");
        if (!string.Equals(agent, engine, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Station Agent and engine release links disagree.");
        }
        return agent;
    }

    private static string ReadLinkReleaseIdentity(
        string linkPath,
        string component)
    {
        DirectoryInfo link = new(linkPath);
        link.Refresh();
        string? targetText = link.LinkTarget;
        if (string.IsNullOrEmpty(targetText))
        {
            throw new InvalidOperationException(
                "A station release component is not a symbolic link.");
        }
        string target = Path.GetFullPath(
            Path.IsPathFullyQualified(targetText)
                ? targetText
                : Path.Combine(Path.GetDirectoryName(linkPath)!, targetText));
        string releaseRoot = Path.GetFullPath(ReleaseRoot);
        if (!target.StartsWith(
                releaseRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFileName(target),
                component,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A station release component link escaped its fixed release root.");
        }
        string? release = Path.GetFileName(Path.GetDirectoryName(target));
        if (!IsReleaseIdentity(release))
        {
            throw new InvalidOperationException(
                "A station release component link contains an invalid release identity.");
        }
        return release!;
    }

    private static string StagingDirectory(string correlationId)
    {
        string root = Path.GetFullPath(StagingRoot);
        string path = Path.GetFullPath(Path.Combine(root, correlationId));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The updater staging directory escaped its fixed root.");
        }
        return path;
    }

    internal static string NormalizeArchiveEntryName(
        string entryName,
        bool isDirectory)
    {
        string normalized = entryName.Replace('\\', '/');
        if (normalized.Length is < 1 or > 512 ||
            normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The station release archive contains an unsafe path.");
        }
        if (isDirectory && normalized is "." or "./")
        {
            return string.Empty;
        }
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        if (isDirectory && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized[..^1];
        }
        if (normalized.Length == 0 ||
            normalized.Split('/').Any(part =>
                part.Length == 0 || part is "." or ".."))
        {
            throw new InvalidDataException(
                "The station release archive contains an unsafe path.");
        }
        return normalized;
    }

    private static string ExactStagingFile(string root, string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The updater staging file escaped its transaction directory.");
        }
        ValidateRegularFile(
            path,
            fileName.EndsWith(".json", StringComparison.Ordinal)
                ? MaximumManifestBytes
                : MaximumPackageBytes);
        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidDataException(
                "A staged release file is writable outside its owner.");
        }
        return path;
    }

    private static string ReleaseDirectory(string releaseIdentity)
    {
        if (!IsReleaseIdentity(releaseIdentity))
        {
            throw new InvalidDataException(
                "The updater release identity is invalid.");
        }
        string root = Path.GetFullPath(ReleaseRoot);
        string path = Path.GetFullPath(Path.Combine(root, releaseIdentity));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The updater release directory escaped its fixed root.");
        }
        return path;
    }

    private static void ValidatePrivateDirectory(string path)
    {
        DirectoryInfo info = new(path);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null)
        {
            throw new InvalidDataException(
                "The station update staging directory is unavailable or unsafe.");
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode &
            (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite |
             UnixFileMode.OtherRead | UnixFileMode.OtherExecute)) != 0)
        {
            throw new InvalidDataException(
                "The station update staging directory permissions are too broad.");
        }
    }

    private static byte[] ReadPinnedKey()
    {
        ValidateRegularFile(TrustKeyPath, 1024);
        ValidateRegularFile(TrustKeyShaPath, 256);
        UnixFileMode forbiddenWrite =
            UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if ((File.GetUnixFileMode(TrustKeyPath) & forbiddenWrite) != 0 ||
            (File.GetUnixFileMode(TrustKeyShaPath) & forbiddenWrite) != 0)
        {
            throw new InvalidDataException(
                "The root updater release trust files are writable outside root ownership.");
        }
        byte[] key = File.ReadAllBytes(TrustKeyPath);
        string fingerprint = File.ReadAllText(TrustKeyShaPath)
            .Trim()
            .ToLowerInvariant();
        if (!Sha256Pattern().IsMatch(fingerprint) ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(key)),
                fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The root updater release verification key fingerprint is invalid.");
        }
        return key;
    }

    private static void ValidateRegularFile(string path, long maximumBytes)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.LinkTarget is not null)
        {
            throw new InvalidDataException(
                "A root updater input file is unavailable or unsafe.");
        }
    }

    private static void VerifyPackage(string path, SignedPackage package)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (info.Length != package.Length)
        {
            throw new InvalidDataException(
                "The root updater package length differs from the signed manifest.");
        }
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (!string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(stream)),
                package.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The root updater package digest differs from the signed manifest.");
        }
    }

    private static SignedPackage RequireRole(
        IReadOnlyDictionary<string, SignedPackage> roles,
        string role) =>
        roles.TryGetValue(role, out SignedPackage? package)
            ? package
            : throw new InvalidDataException(
                $"The root updater manifest is missing {role}.");

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "The root updater manifest contains duplicate properties.");
                }
                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(child);
            }
        }
    }

    private static string RequiredString(
        JsonElement element,
        string property,
        int maximumLength)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"The root updater manifest requires {property}.");
        }
        string text = value.GetString() ?? string.Empty;
        if (text.Length is < 1 || text.Length > maximumLength ||
            text.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"The root updater manifest {property} is invalid.");
        }
        return text;
    }

    private static long RequiredInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long number))
        {
            throw new InvalidDataException(
                $"The root updater manifest requires numeric {property}.");
        }
        return number;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        if (!Base64UrlPattern().IsMatch(value))
        {
            throw new InvalidDataException(
                "The root updater signature encoding is invalid.");
        }
        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The root updater signature encoding is invalid.",
                exception);
        }
    }

    private static UpdateTransaction? ReadTransaction()
    {
        if (!File.Exists(TransactionPath))
        {
            return null;
        }
        ValidateRegularFile(TransactionPath, 16 * 1024);
        UpdateTransaction? transaction =
            JsonSerializer.Deserialize<UpdateTransaction>(
                File.ReadAllBytes(TransactionPath),
                StationProtocol.JsonOptions);
        if (transaction is null ||
            transaction.SchemaVersion != 1 ||
            !IsCorrelationId(transaction.CorrelationId) ||
            !IsReleaseIdentity(transaction.PreviousReleaseIdentity) ||
            !IsReleaseIdentity(transaction.TargetReleaseIdentity) ||
            transaction.StartedAtUtc < DateTimeOffset.UnixEpoch ||
            transaction.ConfirmationDeadlineUtc <= transaction.StartedAtUtc ||
            transaction.ConfirmationDeadlineUtc - transaction.StartedAtUtc >
                TimeSpan.FromMinutes(5))
        {
            throw new InvalidDataException(
                "The root updater transaction state is invalid.");
        }
        return transaction;
    }

    private static void WriteTransaction(UpdateTransaction transaction)
    {
        string? directory = Path.GetDirectoryName(TransactionPath);
        if (directory is null)
        {
            throw new InvalidOperationException(
                "The updater transaction path has no directory.");
        }
        Directory.CreateDirectory(directory);
        string temporary = TransactionPath + ".new";
        if (File.Exists(temporary))
        {
            throw new InvalidOperationException(
                "The updater transaction staging file already exists.");
        }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            transaction,
            StationProtocol.JsonOptions);
        using (FileStream stream = new(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        File.SetUnixFileMode(
            temporary,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, TransactionPath, overwrite: true);
    }

    private static void DeleteTransaction()
    {
        if (File.Exists(TransactionPath))
        {
            File.Delete(TransactionPath);
        }
    }

    private static UpdateCompletion? ReadCompletion()
    {
        if (!File.Exists(CompletionPath))
        {
            return null;
        }
        ValidateRegularFile(CompletionPath, 16 * 1024);
        UpdateCompletion? completion =
            JsonSerializer.Deserialize<UpdateCompletion>(
                File.ReadAllBytes(CompletionPath),
                StationProtocol.JsonOptions);
        if (completion is null ||
            completion.SchemaVersion != 1 ||
            !IsCorrelationId(completion.CorrelationId) ||
            !IsReleaseIdentity(completion.TargetReleaseIdentity) ||
            !IsReleaseIdentity(completion.ActiveReleaseIdentity) ||
            completion.CompletedAtUtc < DateTimeOffset.UnixEpoch ||
            (completion.RolledBack
                ? !string.Equals(
                    completion.Outcome,
                    "startup-rollback",
                    StringComparison.Ordinal) ||
                  string.Equals(
                    completion.TargetReleaseIdentity,
                    completion.ActiveReleaseIdentity,
                    StringComparison.Ordinal)
                : !string.Equals(
                    completion.Outcome,
                    "confirmed",
                    StringComparison.Ordinal) ||
                  !string.Equals(
                    completion.TargetReleaseIdentity,
                    completion.ActiveReleaseIdentity,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The root updater completion state is invalid.");
        }
        return completion;
    }

    private static void WriteCompletion(UpdateCompletion completion)
    {
        UpdateCompletion? existing = ReadCompletion();
        if (existing is not null)
        {
            if (Equals(existing, completion))
            {
                return;
            }
            throw new InvalidOperationException(
                "A prior root updater completion record requires reconciliation.");
        }
        PersistCompletion(completion);
    }

    private static void ReplaceCompletion(UpdateCompletion completion)
    {
        _ = ReadCompletion() ?? throw new InvalidOperationException(
            "The root updater completion record is unavailable for replacement.");
        PersistCompletion(completion);
    }

    private static void PersistCompletion(UpdateCompletion completion)
    {
        string? directory = Path.GetDirectoryName(CompletionPath);
        if (directory is null)
        {
            throw new InvalidOperationException(
                "The updater completion path has no directory.");
        }
        Directory.CreateDirectory(directory);
        string temporary = CompletionPath + ".new";
        if (File.Exists(temporary))
        {
            throw new InvalidOperationException(
                "The updater completion staging file already exists.");
        }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            completion,
            StationProtocol.JsonOptions);
        using (FileStream stream = new(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        File.SetUnixFileMode(
            temporary,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, CompletionPath, overwrite: true);
    }

    private static void DeleteCompletion()
    {
        if (File.Exists(CompletionPath))
        {
            File.Delete(CompletionPath);
        }
    }

    private static void TryDeleteStaging(string correlationId)
    {
        string staging = StagingDirectory(correlationId);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task RunSystemctlAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new()
        {
            FileName = SystemctlPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.ArgumentList.Add("--no-ask-password");
        start.ArgumentList.Add("--no-pager");
        start.ArgumentList.Add("--plain");
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SystemctlTimeout);
        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new IOException("systemctl did not start.");
        }
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout;
            string error = await stderr;
            if (output.Length > 4096 || error.Length > 4096 ||
                process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "systemctl rejected a fixed station release operation.");
            }
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw;
        }
    }

    private static LocalStationReleaseUpdateResult Result(
        LocalStationReleaseUpdateRequest request,
        bool succeeded,
        string outcome,
        string activeRelease,
        string previousRelease,
        bool requiresRestart,
        string? correlationId = null,
        string completedReleaseIdentity = "",
        bool rolledBack = false) =>
        new(
            StationLocalUpdaterMessageTypes.Result,
            correlationId ?? request.CorrelationId,
            request.ReleaseIdentity,
            request.Action,
            succeeded,
            outcome,
            activeRelease,
            previousRelease,
            requiresRestart,
            completedReleaseIdentity,
            rolledBack);

    private static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length is < 2 or > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "The station updater request length is invalid.");
        }
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, StationProtocol.JsonOptions) ??
            throw new InvalidDataException(
                "The station updater request is empty.");
    }

    private static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            StationProtocol.JsonOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "The station updater response is too large.");
        }
        byte[] lengthBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, payload.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void RemoveStaleSocket(string path)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (!info.Exists)
        {
            return;
        }
        if ((info.Attributes &
            (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            info.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                "The station updater socket path is unsafe.");
        }
        File.Delete(path);
    }

    private static void ValidateFixedLayout()
    {
        foreach (string path in
                 new[]
                 {
                     RuntimeDirectory,
                     SocketPath,
                     StagingRoot,
                     ReleaseRoot,
                     TransactionPath,
                     CompletionPath
                 })
        {
            if (!Path.IsPathFullyQualified(path) ||
                !string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The station updater contains a non-canonical fixed path.");
            }
        }
    }

    private static bool IsCorrelationId(string? value) =>
        value is not null && value.Length == 32 &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsReleaseIdentity(string? value) =>
        value is not null && ReleaseIdentityPattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,96}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseIdentityPattern();

    private static (string ExpectedIdentity, string ExpectedFileName)?
        ExpectedPackageDeclaration(string role, string architecture) =>
        role switch
        {
            "gatewayWeb" => (
                "gateway-web",
                $"packages/aethersdr-gateway-{architecture}.tar.gz"),
            "broker" => (
                "broker",
                $"packages/aethersdr-broker-{architecture}.tar.gz"),
            "aetherRemoteAgent" => (
                "aetherremote-agent",
                $"packages/aetherremote-agent-{architecture}.tar.gz"),
            "stationEngine" => (
                "station-engine",
                $"packages/aethersdr-station-engine-{architecture}.tar.gz"),
            _ => null
        };

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{40,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64UrlPattern();

    private sealed record SignedPackage(
        string FileName,
        string Sha256,
        long Length);

    private sealed record VerifiedBundle(
        string ReleaseIdentity,
        string Version,
        string AgentArchivePath,
        string EngineArchivePath);

    private sealed record UpdateTransaction(
        int SchemaVersion,
        string CorrelationId,
        string PreviousReleaseIdentity,
        string TargetReleaseIdentity,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset ConfirmationDeadlineUtc);

    private sealed record UpdateCompletion(
        int SchemaVersion,
        string CorrelationId,
        string TargetReleaseIdentity,
        string ActiveReleaseIdentity,
        string Outcome,
        bool RolledBack,
        DateTimeOffset CompletedAtUtc,
        bool Acknowledged = false);
}
