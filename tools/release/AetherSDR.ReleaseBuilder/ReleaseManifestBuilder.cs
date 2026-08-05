using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.ReleaseBuilder;

public enum ReleaseBuilderChannel
{
    Stable = 1,
    Beta = 2,
    Pinned = 3
}

public enum ReleaseBuilderArchitecture
{
    LinuxX64 = 1,
    LinuxArm64 = 2
}

public enum ReleaseManifestBuildFailureCode
{
    None = 0,
    InvalidRequest = 1,
    InvalidPackageSet = 2,
    InvalidSigningKey = 3,
    SigningFailed = 4,
    SelfVerificationFailed = 5,
    OutputWriteFailed = 6
}

public sealed record ReleaseManifestBuildRequest(
    string AssetDirectory,
    string OutputManifestPath,
    string PrivateKeyPath,
    string KeyId,
    string Version,
    ReleaseBuilderChannel Channel,
    ReleaseBuilderArchitecture Architecture,
    string MinimumPreviousVersion,
    int TargetConfigurationSchemaVersion,
    int MinimumCompatibleConfigurationSchemaVersion,
    int MaximumCompatibleConfigurationSchemaVersion,
    int MinimumProtocolVersion,
    int MaximumProtocolVersion,
    string ReleaseTitle,
    string ReleaseSummary);

public sealed record ReleaseManifestBuildReport(
    int ReportVersion,
    bool Succeeded,
    int ExitCode,
    ReleaseManifestBuildFailureCode FailureCode,
    string Message,
    string ReleaseIdentity,
    string Version,
    string Channel,
    string Architecture,
    int PackageCount,
    long TotalPackageBytes);

public sealed class ReleaseManifestBuilder
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 2;

    private const int CurrentReportVersion = 1;
    private const int MaximumPrivateKeyBytes = 16 * 1024;
    private const int MaximumKeyIdLength = 64;
    private const int MaximumReleaseTitleLength = 120;
    private const int MaximumReleaseSummaryLength = 2048;
    private const UnixFileMode ForbiddenPrivateKeyModes =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute |
        UnixFileMode.UserExecute;
    private const UnixFileMode ForbiddenParentDirectoryModes =
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;

    private static readonly PackageDefinition[] PackageDefinitions =
    [
        new("gateway-web", ReleasePackageRole.GatewayWeb, "aethersdr-gateway"),
        new("broker", ReleasePackageRole.Broker, "aethersdr-broker"),
        new(
            "aetherremote-agent",
            ReleasePackageRole.AetherRemoteAgent,
            "aetherremote-agent"),
        new(
            "station-engine",
            ReleasePackageRole.StationEngine,
            "aethersdr-station-engine")
    ];

    public ReleaseManifestBuildReport Build(ReleaseManifestBuildRequest request)
    {
        try
        {
            ValidatedRequest validated = ValidateRequest(request);
            PackageSnapshot[] packages = ReadPackages(validated);
            using LoadedSigningKey signingKey = LoadSigningKey(
                validated.PrivateKeyPath,
                validated.KeyId);
            byte[] manifest = CreateAndVerifyManifest(
                validated,
                packages,
                signingKey);
            try
            {
                WriteManifestAtomically(validated.OutputManifestPath, manifest);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(manifest);
            }

            return new ReleaseManifestBuildReport(
                CurrentReportVersion,
                Succeeded: true,
                SuccessExitCode,
                ReleaseManifestBuildFailureCode.None,
                "The signed architecture release manifest was created and self-verified.",
                validated.ReleaseIdentity,
                validated.Version,
                validated.Channel.ToString(),
                validated.ArchitectureName,
                packages.Length,
                packages.Sum(package => package.Length));
        }
        catch (ReleaseBuildException exception)
        {
            return CreateFailureReport(exception.FailureCode, exception.Message);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or CryptographicException or ArgumentException or
                NotSupportedException or PathTooLongException or JsonException)
        {
            return CreateFailureReport(
                ReleaseManifestBuildFailureCode.SigningFailed,
                "The signed architecture release manifest could not be produced.");
        }
    }

    private static ReleaseManifestBuildReport CreateFailureReport(
        ReleaseManifestBuildFailureCode failureCode,
        string message) =>
        new(
            CurrentReportVersion,
            Succeeded: false,
            FailureExitCode,
            failureCode,
            message,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0);

    private static ValidatedRequest ValidateRequest(ReleaseManifestBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string assetDirectory = ValidateCanonicalDirectory(request.AssetDirectory);
        string outputManifestPath = ValidateCanonicalOutputPath(
            request.OutputManifestPath,
            assetDirectory,
            request.Architecture);
        string privateKeyPath = ValidateCanonicalFilePath(request.PrivateKeyPath);
        string keyId = ValidateKeyId(request.KeyId);

        if (!ReleaseSemanticVersion.TryParse(
                request.Version,
                out ReleaseSemanticVersion version) ||
            !string.Equals(
                request.Version,
                FormatSemanticVersion(version),
                StringComparison.Ordinal))
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The release version must be one canonical semantic version.");
        }
        if (!ReleaseSemanticVersion.TryParse(
                request.MinimumPreviousVersion,
                out ReleaseSemanticVersion minimumPreviousVersion) ||
            !string.Equals(
                request.MinimumPreviousVersion,
                FormatSemanticVersion(minimumPreviousVersion),
                StringComparison.Ordinal) ||
            minimumPreviousVersion.CompareTo(version) >= 0)
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The minimum previous version must be canonical and older than the target release.");
        }

        switch (request.Channel)
        {
            case ReleaseBuilderChannel.Stable when version.IsPrerelease:
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidRequest,
                    "A Stable release cannot use a prerelease semantic version.");
            case ReleaseBuilderChannel.Beta when !version.IsPrerelease:
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidRequest,
                    "A Beta release requires a prerelease semantic version.");
            case ReleaseBuilderChannel.Stable or ReleaseBuilderChannel.Beta or
                ReleaseBuilderChannel.Pinned:
                break;
            default:
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidRequest,
                    "The release channel is unsupported.");
        }

        if (request.Architecture is not ReleaseBuilderArchitecture.LinuxX64 and
            not ReleaseBuilderArchitecture.LinuxArm64)
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The release architecture is unsupported.");
        }

        if (request.MinimumCompatibleConfigurationSchemaVersion < 1 ||
            request.MaximumCompatibleConfigurationSchemaVersion <
                request.MinimumCompatibleConfigurationSchemaVersion ||
            request.TargetConfigurationSchemaVersion <
                request.MinimumCompatibleConfigurationSchemaVersion ||
            request.TargetConfigurationSchemaVersion >
                request.MaximumCompatibleConfigurationSchemaVersion)
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The configuration schema compatibility range is invalid.");
        }
        if (request.MinimumProtocolVersion < 1 ||
            request.MaximumProtocolVersion < request.MinimumProtocolVersion)
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The protocol compatibility range is invalid.");
        }

        string title = ValidateText(
            request.ReleaseTitle,
            minimumLength: 1,
            MaximumReleaseTitleLength,
            "release title");
        string summary = ValidateText(
            request.ReleaseSummary,
            minimumLength: 0,
            MaximumReleaseSummaryLength,
            "release summary");
        string architectureName = ArchitectureName(request.Architecture);
        return new ValidatedRequest(
            assetDirectory,
            outputManifestPath,
            privateKeyPath,
            keyId,
            request.Version,
            $"aethersdr-{request.Version}",
            request.Channel,
            request.Architecture,
            architectureName,
            request.MinimumPreviousVersion,
            request.TargetConfigurationSchemaVersion,
            request.MinimumCompatibleConfigurationSchemaVersion,
            request.MaximumCompatibleConfigurationSchemaVersion,
            request.MinimumProtocolVersion,
            request.MaximumProtocolVersion,
            title,
            summary);
    }

    private static PackageSnapshot[] ReadPackages(ValidatedRequest request)
    {
        DirectoryInfo directory = new(request.AssetDirectory);
        directory.Refresh();
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidPackageSet,
                "The release asset directory is missing or unsafe.");
        }

        string[] expectedNames = PackageDefinitions
            .Select(definition =>
                $"{definition.FileStem}-{request.ArchitectureName}.tar.gz")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        string[] actualNames = directory
            .EnumerateFileSystemInfos()
            .Select(entry => entry.Name)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal))
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidPackageSet,
                "The architecture asset directory must contain exactly the four required package archives.");
        }

        List<PackageSnapshot> packages = [];
        foreach (PackageDefinition definition in PackageDefinitions)
        {
            string fileName =
                $"{definition.FileStem}-{request.ArchitectureName}.tar.gz";
            string path = Path.Combine(request.AssetDirectory, fileName);
            FileInfo file = new(path);
            file.Refresh();
            if (!file.Exists || file.LinkTarget is not null ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (file.Attributes & FileAttributes.Directory) != 0 ||
                file.Length is < 1 or >
                    SignedReleaseManifestVerifier.MaximumDeclaredPackageLength)
            {
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidPackageSet,
                    "A required release package is missing, empty, oversized, or unsafe.");
            }

            long length = file.Length;
            DateTime lastWrite = file.LastWriteTimeUtc;
            byte[] digest;
            using (FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan))
            {
                digest = SHA256.HashData(stream);
            }
            file.Refresh();
            if (!file.Exists || file.Length != length ||
                file.LastWriteTimeUtc != lastWrite)
            {
                CryptographicOperations.ZeroMemory(digest);
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidPackageSet,
                    "A required release package changed while it was being hashed.");
            }
            packages.Add(new PackageSnapshot(
                definition.PackageIdentity,
                definition.Role,
                fileName,
                length,
                digest));
        }
        return [.. packages];
    }

    private static LoadedSigningKey LoadSigningKey(string path, string keyId)
    {
        FileInfo file = new(path);
        file.Refresh();
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            (file.Attributes & FileAttributes.Directory) != 0 ||
            file.Length is < 1 or > MaximumPrivateKeyBytes)
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidSigningKey,
                "The release signing key file is missing, unsafe, empty, or oversized.");
        }

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & ForbiddenPrivateKeyModes) != 0 ||
                (mode & UnixFileMode.UserRead) == 0)
            {
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidSigningKey,
                    "The release signing key file must be owner-readable and inaccessible to group and other users.");
            }
            string parent = Path.GetDirectoryName(path) ?? string.Empty;
            UnixFileMode parentMode = File.GetUnixFileMode(parent);
            if ((parentMode & ForbiddenParentDirectoryModes) != 0)
            {
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidSigningKey,
                    "The release signing key directory must not be writable by group or other users.");
            }
        }

        byte[] bytes = File.ReadAllBytes(path);
        char[] characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int characterCount = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true)
                .GetChars(bytes, 0, bytes.Length, characters, 0);
            ReadOnlySpan<char> pem = characters.AsSpan(0, characterCount).Trim();
            const string Begin = "-----BEGIN PRIVATE KEY-----";
            const string End = "-----END PRIVATE KEY-----";
            if (!pem.StartsWith(Begin, StringComparison.Ordinal) ||
                !pem.EndsWith(End, StringComparison.Ordinal) ||
                pem[Begin.Length..].IndexOf(Begin, StringComparison.Ordinal) >= 0)
            {
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidSigningKey,
                    "The release signing key must contain exactly one PKCS#8 PRIVATE KEY PEM block.");
            }

            ECDsa key = ECDsa.Create();
            try
            {
                key.ImportFromPem(pem);
                if (key.KeySize != 256)
                {
                    throw Failure(
                        ReleaseManifestBuildFailureCode.InvalidSigningKey,
                        "The release signing key must be ECDSA P-256.");
                }
                return new LoadedSigningKey(keyId, key);
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }
        catch (ReleaseBuildException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException or CryptographicException or
                DecoderFallbackException)
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidSigningKey,
                "The release signing key could not be parsed as ECDSA P-256 PKCS#8 PEM.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Array.Clear(characters);
        }
    }

    private static byte[] CreateAndVerifyManifest(
        ValidatedRequest request,
        IReadOnlyList<PackageSnapshot> packages,
        LoadedSigningKey signingKey)
    {
        SignedReleaseManifestPayload payload = new()
        {
            SchemaVersion = SignedReleaseManifestPayload.CurrentSchemaVersion,
            ReleaseIdentity = request.ReleaseIdentity,
            Version = request.Version,
            Channel = request.Channel switch
            {
                ReleaseBuilderChannel.Stable => ReleaseManifestChannel.Stable,
                ReleaseBuilderChannel.Beta => ReleaseManifestChannel.Beta,
                ReleaseBuilderChannel.Pinned => ReleaseManifestChannel.Pinned,
                _ => throw new InvalidOperationException()
            },
            Architecture = request.Architecture switch
            {
                ReleaseBuilderArchitecture.LinuxX64 =>
                    ReleaseManifestArchitecture.LinuxX64,
                ReleaseBuilderArchitecture.LinuxArm64 =>
                    ReleaseManifestArchitecture.LinuxArm64,
                _ => throw new InvalidOperationException()
            },
            Packages = packages.Select(package => new SignedReleasePackage
            {
                PackageIdentity = package.PackageIdentity,
                Role = package.Role,
                FileName = $"packages/{package.FileName}",
                Length = package.Length,
                Sha256 = Convert.ToHexString(package.Sha256).ToLowerInvariant()
            }).ToArray(),
            Configuration = new ReleaseConfigurationCompatibility
            {
                TargetSchemaVersion = request.TargetConfigurationSchemaVersion,
                MinimumCompatibleSchemaVersion =
                    request.MinimumCompatibleConfigurationSchemaVersion,
                MaximumCompatibleSchemaVersion =
                    request.MaximumCompatibleConfigurationSchemaVersion
            },
            Protocol = new ReleaseProtocolCompatibility
            {
                MinimumVersion = request.MinimumProtocolVersion,
                MaximumVersion = request.MaximumProtocolVersion
            },
            MinimumPreviousVersion = request.MinimumPreviousVersion,
            Restart = new ReleaseRestartDeclaration
            {
                GatewayWeb = true,
                Broker = true,
                AetherRemoteAgent = true,
                StationEngine = true,
                Host = false
            },
            Migration = new ReleaseMigrationDeclaration
            {
                Kind = ReleaseMigrationKind.None,
                FromConfigurationSchemaVersion = null,
                ToConfigurationSchemaVersion = null,
                MigrationIdentity = string.Empty
            },
            TxSupport = new ReleaseTxSupportDeclaration
            {
                DeclarationVersion =
                    ReleaseTxSupportDeclaration.CurrentDeclarationVersion,
                Capability = ReleaseTxSupportCapability.Available,
                EnablesTransmit = false,
                GrantsTransmitEligibility = false,
                CreatesBrowserTransmitAuthority = false,
                ArmsWatchdog = false
            },
            ReleaseNotes = new ReleaseNotesMetadata
            {
                Title = request.ReleaseTitle,
                Summary = request.ReleaseSummary
            }
        };

        byte[] signingBytes = SignedReleaseManifestJson.CreateSigningBytes(
            payload,
            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
            signingKey.KeyId);
        byte[] signature;
        try
        {
            signature = signingKey.Key.SignData(
                signingBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException exception)
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.SigningFailed,
                "The release manifest signature could not be created.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingBytes);
        }

        SignedReleaseManifestDocument document = new()
        {
            Payload = payload,
            Signature = new ReleaseManifestSignature
            {
                Algorithm = ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                KeyId = signingKey.KeyId,
                Value = ToBase64Url(signature)
            }
        };
        CryptographicOperations.ZeroMemory(signature);
        byte[] manifest = SignedReleaseManifestJson.Serialize(document);

        byte[] publicKey = signingKey.Key.ExportSubjectPublicKeyInfo();
        try
        {
            LocalImmutableReleasePackage[] localPackages = packages
                .Select(package => new LocalImmutableReleasePackage(
                    $"packages/{package.FileName}",
                    package.Length,
                    package.Sha256))
                .ToArray();
            ReleaseManifestVerificationContext context = new(
                payload.Architecture,
                request.Channel switch
                {
                    ReleaseBuilderChannel.Stable => InstallationUpdateChannel.Stable,
                    ReleaseBuilderChannel.Beta => InstallationUpdateChannel.Beta,
                    ReleaseBuilderChannel.Pinned => InstallationUpdateChannel.Pinned,
                    _ => throw new InvalidOperationException()
                },
                request.Channel == ReleaseBuilderChannel.Pinned
                    ? request.ReleaseIdentity
                    : string.Empty,
                request.MinimumPreviousVersion,
                request.TargetConfigurationSchemaVersion,
                request.MinimumProtocolVersion);
            ReleaseManifestVerificationReport verification =
                new SignedReleaseManifestVerifier().Verify(
                    manifest,
                    localPackages,
                    context,
                    [
                        new ReleaseManifestVerificationKey(
                            signingKey.KeyId,
                            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                            publicKey)
                    ]);
            if (!verification.Succeeded)
            {
                CryptographicOperations.ZeroMemory(manifest);
                throw Failure(
                    ReleaseManifestBuildFailureCode.SelfVerificationFailed,
                    "The generated release manifest failed production self-verification.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
            foreach (PackageSnapshot package in packages)
            {
                package.Dispose();
            }
        }

        return manifest;
    }

    private static void WriteManifestAtomically(string path, byte[] manifest)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(manifest);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temporaryPath, path, overwrite: false);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or NotSupportedException)
        {
            TryDelete(temporaryPath);
            throw Failure(
                ReleaseManifestBuildFailureCode.OutputWriteFailed,
                "The signed release manifest could not be written atomically.",
                exception);
        }
    }

    private static string ValidateCanonicalDirectory(string? value)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length is 0 or > 1024 ||
            !string.Equals(path, value, StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The asset directory must be one canonical absolute path.");
        }
        string fullPath = Path.GetFullPath(path);
        if (!PathsEqual(path, fullPath))
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The asset directory path must not contain relative segments.");
        }
        return fullPath;
    }

    private static string ValidateCanonicalOutputPath(
        string? value,
        string assetDirectory,
        ReleaseBuilderArchitecture architecture)
    {
        string path = ValidateCanonicalFilePath(value);
        string expectedName =
            $"release-manifest-{ArchitectureName(architecture)}.json";
        if (!string.Equals(
                Path.GetFileName(path),
                expectedName,
                StringComparison.Ordinal) ||
            !PathsEqual(Path.GetDirectoryName(path) ?? string.Empty, assetDirectory) ||
            File.Exists(path) || Directory.Exists(path))
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The output manifest must use the exact architecture asset name in the asset directory and must not already exist.");
        }
        return path;
    }

    private static string ValidateCanonicalFilePath(string? value)
    {
        string path = value?.Trim() ?? string.Empty;
        if (path.Length is 0 or > 1024 ||
            !string.Equals(path, value, StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "A required file path must be canonical and absolute.");
        }
        string fullPath = Path.GetFullPath(path);
        if (!PathsEqual(path, fullPath))
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "A required file path must not contain relative segments.");
        }
        return fullPath;
    }

    private static string ValidateKeyId(string? value)
    {
        string keyId = value?.Trim() ?? string.Empty;
        if (keyId.Length is 0 or > MaximumKeyIdLength ||
            !string.Equals(value, keyId, StringComparison.Ordinal))
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The release signing key ID is invalid.");
        }
        foreach (char character in keyId)
        {
            if (!(char.IsAsciiLetterOrDigit(character) ||
                    character is '-' or '_' or '.'))
            {
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidRequest,
                    "The release signing key ID contains an unsupported character.");
            }
        }
        return keyId;
    }

    private static string ValidateText(
        string? value,
        int minimumLength,
        int maximumLength,
        string name)
    {
        string text = value ?? string.Empty;
        if (text.Length < minimumLength || text.Length > maximumLength ||
            !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                $"The {name} is missing, non-canonical, or exceeds its bound.");
        }
        foreach (char character in text)
        {
            if (char.IsControl(character) && character is not '\n' and not '\r' and
                not '\t')
            {
                throw Failure(
                    ReleaseManifestBuildFailureCode.InvalidRequest,
                    $"The {name} contains an unsupported control character.");
            }
        }
        return text;
    }

    private static string FormatSemanticVersion(ReleaseSemanticVersion version)
    {
        string value = $"{version.Major}.{version.Minor}.{version.Patch}";
        if (version.Prerelease.Length > 0)
        {
            value += $"-{version.Prerelease}";
        }
        if (version.BuildMetadata.Length > 0)
        {
            value += $"+{version.BuildMetadata}";
        }
        return value;
    }

    private static string ArchitectureName(ReleaseBuilderArchitecture architecture) =>
        architecture switch
        {
            ReleaseBuilderArchitecture.LinuxX64 => "linux-x64",
            ReleaseBuilderArchitecture.LinuxArm64 => "linux-arm64",
            _ => throw Failure(
                ReleaseManifestBuildFailureCode.InvalidRequest,
                "The release architecture is unsupported.")
        };

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string ToBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException)
        {
        }
    }

    private static ReleaseBuildException Failure(
        ReleaseManifestBuildFailureCode failureCode,
        string message,
        Exception? innerException = null) =>
        new(failureCode, message, innerException);

    private sealed record ValidatedRequest(
        string AssetDirectory,
        string OutputManifestPath,
        string PrivateKeyPath,
        string KeyId,
        string Version,
        string ReleaseIdentity,
        ReleaseBuilderChannel Channel,
        ReleaseBuilderArchitecture Architecture,
        string ArchitectureName,
        string MinimumPreviousVersion,
        int TargetConfigurationSchemaVersion,
        int MinimumCompatibleConfigurationSchemaVersion,
        int MaximumCompatibleConfigurationSchemaVersion,
        int MinimumProtocolVersion,
        int MaximumProtocolVersion,
        string ReleaseTitle,
        string ReleaseSummary);

    private sealed record PackageDefinition(
        string PackageIdentity,
        ReleasePackageRole Role,
        string FileStem);

    private sealed class PackageSnapshot : IDisposable
    {
        private readonly byte[] m_sha256;

        internal PackageSnapshot(
            string packageIdentity,
            ReleasePackageRole role,
            string fileName,
            long length,
            byte[] sha256)
        {
            PackageIdentity = packageIdentity;
            Role = role;
            FileName = fileName;
            Length = length;
            m_sha256 = sha256;
        }

        internal string PackageIdentity { get; }
        internal ReleasePackageRole Role { get; }
        internal string FileName { get; }
        internal long Length { get; }
        internal ReadOnlySpan<byte> Sha256 => m_sha256;

        public void Dispose() => CryptographicOperations.ZeroMemory(m_sha256);
    }

    private sealed class LoadedSigningKey : IDisposable
    {
        internal LoadedSigningKey(string keyId, ECDsa key)
        {
            KeyId = keyId;
            Key = key;
        }

        internal string KeyId { get; }
        internal ECDsa Key { get; }

        public void Dispose() => Key.Dispose();
    }

    private sealed class ReleaseBuildException : Exception
    {
        internal ReleaseBuildException(
            ReleaseManifestBuildFailureCode failureCode,
            string message,
            Exception? innerException)
            : base(message, innerException)
        {
            FailureCode = failureCode;
        }

        internal ReleaseManifestBuildFailureCode FailureCode { get; }
    }
}

public static class ReleaseBuilderConsole
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        ReleaseManifestBuildRequest request;
        try
        {
            request = ReleaseBuilderCommandLine.Parse(arguments);
        }
        catch (InvalidOperationException exception)
        {
            ReleaseManifestBuildReport failure = new(
                ReportVersion: 1,
                Succeeded: false,
                ExitCode: ReleaseManifestBuilder.FailureExitCode,
                ReleaseManifestBuildFailureCode.InvalidRequest,
                exception.Message,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0);
            await output.WriteLineAsync(JsonSerializer.Serialize(failure, JsonOptions));
            return failure.ExitCode;
        }

        ReleaseManifestBuildReport report = new ReleaseManifestBuilder().Build(request);
        await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions));
        return report.ExitCode;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}

public static class ReleaseBuilderCommandLine
{
    public const string AssetDirectorySwitch = "--asset-directory";
    public const string OutputManifestSwitch = "--output-manifest";
    public const string PrivateKeySwitch = "--private-key";
    public const string KeyIdSwitch = "--key-id";
    public const string VersionSwitch = "--version";
    public const string ChannelSwitch = "--channel";
    public const string ArchitectureSwitch = "--architecture";
    public const string MinimumPreviousVersionSwitch = "--minimum-previous-version";
    public const string TargetConfigurationSchemaSwitch =
        "--target-configuration-schema-version";
    public const string MinimumConfigurationSchemaSwitch =
        "--minimum-configuration-schema-version";
    public const string MaximumConfigurationSchemaSwitch =
        "--maximum-configuration-schema-version";
    public const string MinimumProtocolSwitch = "--minimum-protocol-version";
    public const string MaximumProtocolSwitch = "--maximum-protocol-version";
    public const string ReleaseTitleSwitch = "--release-title";
    public const string ReleaseSummarySwitch = "--release-summary";

    public static ReleaseManifestBuildRequest Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Count; index++)
        {
            string option = arguments[index];
            if (!KnownOptions.Contains(option) || values.ContainsKey(option) ||
                index + 1 >= arguments.Count ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Release builder arguments are missing, duplicated, or unsupported.");
            }
            values.Add(option, arguments[++index]);
        }
        if (values.Count != KnownOptions.Length)
        {
            throw new InvalidOperationException(
                "Every release builder option is required exactly once.");
        }

        return new ReleaseManifestBuildRequest(
            values[AssetDirectorySwitch],
            values[OutputManifestSwitch],
            values[PrivateKeySwitch],
            values[KeyIdSwitch],
            values[VersionSwitch],
            ParseChannel(values[ChannelSwitch]),
            ParseArchitecture(values[ArchitectureSwitch]),
            values[MinimumPreviousVersionSwitch],
            ParsePositiveInteger(values[TargetConfigurationSchemaSwitch]),
            ParsePositiveInteger(values[MinimumConfigurationSchemaSwitch]),
            ParsePositiveInteger(values[MaximumConfigurationSchemaSwitch]),
            ParsePositiveInteger(values[MinimumProtocolSwitch]),
            ParsePositiveInteger(values[MaximumProtocolSwitch]),
            values[ReleaseTitleSwitch],
            values[ReleaseSummarySwitch]);
    }

    private static readonly string[] KnownOptions =
    [
        AssetDirectorySwitch,
        OutputManifestSwitch,
        PrivateKeySwitch,
        KeyIdSwitch,
        VersionSwitch,
        ChannelSwitch,
        ArchitectureSwitch,
        MinimumPreviousVersionSwitch,
        TargetConfigurationSchemaSwitch,
        MinimumConfigurationSchemaSwitch,
        MaximumConfigurationSchemaSwitch,
        MinimumProtocolSwitch,
        MaximumProtocolSwitch,
        ReleaseTitleSwitch,
        ReleaseSummarySwitch
    ];

    private static ReleaseBuilderChannel ParseChannel(string value) =>
        value switch
        {
            "stable" => ReleaseBuilderChannel.Stable,
            "beta" => ReleaseBuilderChannel.Beta,
            "pinned" => ReleaseBuilderChannel.Pinned,
            _ => throw new InvalidOperationException(
                "The release builder channel must be stable, beta, or pinned.")
        };

    private static ReleaseBuilderArchitecture ParseArchitecture(string value) =>
        value switch
        {
            "linux-x64" => ReleaseBuilderArchitecture.LinuxX64,
            "linux-arm64" => ReleaseBuilderArchitecture.LinuxArm64,
            _ => throw new InvalidOperationException(
                "The release builder architecture must be linux-x64 or linux-arm64.")
        };

    private static int ParsePositiveInteger(string value)
    {
        if (!int.TryParse(value, out int parsed) || parsed < 1 ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Release builder compatibility versions must be canonical positive integers.");
        }
        return parsed;
    }
}
