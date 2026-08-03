using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public enum ReleaseManifestArchitecture
{
    Unknown = 0,
    LinuxX64 = 1,
    LinuxArm64 = 2
}

public enum ReleaseManifestChannel
{
    Unknown = 0,
    Stable = 1,
    Beta = 2,
    Pinned = 3
}

public enum ReleasePackageRole
{
    Unknown = 0,
    GatewayWeb = 1,
    Broker = 2,
    AetherRemoteAgent = 3,
    StationEngine = 4
}

public enum ReleaseManifestSignatureAlgorithm
{
    Unknown = 0,
    EcdsaP256Sha256 = 1,
    RsaPssSha256 = 2
}

public enum ReleaseMigrationKind
{
    Unknown = 0,
    None = 1,
    Required = 2
}

public enum ReleaseTxSupportCapability
{
    Unknown = 0,
    None = 1,
    Available = 2
}

public sealed record SignedReleaseManifestDocument
{
    public required SignedReleaseManifestPayload Payload { get; init; }
    public required ReleaseManifestSignature Signature { get; init; }
}

public sealed record SignedReleaseManifestPayload
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required string ReleaseIdentity { get; init; }
    public required string Version { get; init; }
    public required ReleaseManifestChannel Channel { get; init; }
    public required ReleaseManifestArchitecture Architecture { get; init; }
    public required SignedReleasePackage[] Packages { get; init; }
    public required ReleaseConfigurationCompatibility Configuration { get; init; }
    public required ReleaseProtocolCompatibility Protocol { get; init; }
    public required string MinimumPreviousVersion { get; init; }
    public required ReleaseRestartDeclaration Restart { get; init; }
    public required ReleaseMigrationDeclaration Migration { get; init; }
    public required ReleaseTxSupportDeclaration TxSupport { get; init; }
    public required ReleaseNotesMetadata ReleaseNotes { get; init; }
}

public sealed record SignedReleasePackage
{
    public required string PackageIdentity { get; init; }
    public required ReleasePackageRole Role { get; init; }
    public required string FileName { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record ReleaseConfigurationCompatibility
{
    public required int TargetSchemaVersion { get; init; }
    public required int MinimumCompatibleSchemaVersion { get; init; }
    public required int MaximumCompatibleSchemaVersion { get; init; }
}

public sealed record ReleaseProtocolCompatibility
{
    public required int MinimumVersion { get; init; }
    public required int MaximumVersion { get; init; }
}

public sealed record ReleaseRestartDeclaration
{
    public required bool GatewayWeb { get; init; }
    public required bool Broker { get; init; }
    public required bool AetherRemoteAgent { get; init; }
    public required bool StationEngine { get; init; }
    public required bool Host { get; init; }
}

public sealed record ReleaseMigrationDeclaration
{
    public required ReleaseMigrationKind Kind { get; init; }
    public int? FromConfigurationSchemaVersion { get; init; }
    public int? ToConfigurationSchemaVersion { get; init; }
    public required string MigrationIdentity { get; init; }
}

public sealed record ReleaseTxSupportDeclaration
{
    public const int CurrentDeclarationVersion = 1;

    public required int DeclarationVersion { get; init; }
    public required ReleaseTxSupportCapability Capability { get; init; }
    public required bool EnablesTransmit { get; init; }
    public required bool GrantsTransmitEligibility { get; init; }
    public required bool CreatesBrowserTransmitAuthority { get; init; }
    public required bool ArmsWatchdog { get; init; }
}

public sealed record ReleaseNotesMetadata
{
    public required string Title { get; init; }
    public required string Summary { get; init; }
}

public sealed record ReleaseManifestSignature
{
    public required ReleaseManifestSignatureAlgorithm Algorithm { get; init; }
    public required string KeyId { get; init; }
    public required string Value { get; init; }
}

public sealed record ReleaseManifestVerificationContext(
    ReleaseManifestArchitecture Architecture,
    InstallationUpdateChannel UpdateChannel,
    string PinnedReleaseIdentity,
    string InstalledVersion,
    int ConfigurationSchemaVersion,
    int ProtocolVersion);

public sealed class LocalImmutableReleasePackage
{
    private readonly byte[] m_content;
    private readonly byte[] m_sha256;
    private readonly long m_length;

    public LocalImmutableReleasePackage(
        string relativePath,
        ReadOnlySpan<byte> content)
    {
        RelativePath = relativePath ?? string.Empty;
        m_content = content.ToArray();
        m_length = m_content.LongLength;
        m_sha256 = SHA256.HashData(m_content);
    }

    internal LocalImmutableReleasePackage(
        string relativePath,
        long length,
        ReadOnlySpan<byte> sha256)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        if (sha256.Length != 32)
        {
            throw new ArgumentException(
                "An immutable release package requires one SHA-256 digest.",
                nameof(sha256));
        }

        RelativePath = relativePath ?? string.Empty;
        m_content = [];
        m_length = length;
        m_sha256 = sha256.ToArray();
    }

    public string RelativePath { get; }
    public long Length => m_length;
    internal ReadOnlySpan<byte> Content => m_content;
    internal ReadOnlySpan<byte> Sha256 => m_sha256;
}

internal static class ReleasePackagePath
{
    internal const int MaximumLength = 240;

    internal static bool IsSafe(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaximumLength ||
            !string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
            path[0] == '/' || path.Contains('\\', StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal) ||
            Path.IsPathRooted(path))
        {
            return false;
        }

        foreach (string segment in path.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
            foreach (char character in segment)
            {
                if (char.IsControl(character))
                {
                    return false;
                }
            }
        }
        return true;
    }
}

public sealed class ReleaseManifestVerificationKey
{
    private readonly byte[] m_subjectPublicKeyInfo;

    public ReleaseManifestVerificationKey(
        string keyId,
        ReleaseManifestSignatureAlgorithm algorithm,
        ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        KeyId = keyId ?? string.Empty;
        Algorithm = algorithm;
        m_subjectPublicKeyInfo = subjectPublicKeyInfo.ToArray();
    }

    public string KeyId { get; }
    public ReleaseManifestSignatureAlgorithm Algorithm { get; }
    internal ReadOnlySpan<byte> SubjectPublicKeyInfo => m_subjectPublicKeyInfo;
}

public enum ReleaseManifestFailureCode
{
    None = 0,
    MalformedManifest = 1,
    UnsupportedManifestSchema = 2,
    UnsupportedSignatureAlgorithm = 3,
    UnknownVerificationKey = 4,
    InvalidVerificationKey = 5,
    InvalidSignature = 6,
    InvalidReleaseIdentity = 7,
    InvalidSemanticVersion = 8,
    InvalidChannelRelationship = 9,
    UnsupportedArchitecture = 10,
    DuplicatePackageIdentity = 11,
    DuplicatePackagePath = 12,
    DuplicatePackageRole = 13,
    InvalidPackagePath = 14,
    MissingPackageRole = 15,
    UnexpectedPackageRole = 16,
    InvalidPackageDeclaration = 17,
    MissingPackageInput = 18,
    UnexpectedPackageInput = 19,
    PackageSizeMismatch = 20,
    PackageSha256Mismatch = 21,
    IncompatibleConfigurationSchema = 22,
    IncompatibleProtocolVersion = 23,
    UnsupportedPreviousVersionTransition = 24,
    ContradictoryRestartDeclaration = 25,
    InvalidMigrationDeclaration = 26,
    InvalidTxSupportDeclaration = 27,
    InvalidReleaseNotes = 28,
    VerificationTrustDisabled = 29,
    VerificationTrustUnavailable = 30
}

public sealed record ReleaseManifestVerificationReport(
    bool Succeeded,
    ReleaseManifestFailureCode FailureCode,
    string Message,
    string ReleaseIdentity,
    string Version,
    ReleaseManifestArchitecture? Architecture,
    ReleaseManifestChannel? Channel,
    int DeclaredPackageCount,
    bool TxSupportCapable)
{
    internal static ReleaseManifestVerificationReport Failure(
        ReleaseManifestFailureCode failureCode,
        string message,
        SignedReleaseManifestPayload? trustedPayload = null) =>
        new(
            false,
            failureCode,
            message,
            trustedPayload?.ReleaseIdentity ?? string.Empty,
            trustedPayload?.Version ?? string.Empty,
            trustedPayload?.Architecture,
            trustedPayload?.Channel,
            trustedPayload?.Packages?.Length ?? 0,
            trustedPayload?.TxSupport?.Capability ==
                ReleaseTxSupportCapability.Available);

    internal static ReleaseManifestVerificationReport Success(
        SignedReleaseManifestPayload payload) =>
        new(
            true,
            ReleaseManifestFailureCode.None,
            "The signed local release manifest and all declared packages verified.",
            payload.ReleaseIdentity,
            payload.Version,
            payload.Architecture,
            payload.Channel,
            payload.Packages.Length,
            payload.TxSupport.Capability == ReleaseTxSupportCapability.Available);
}

internal sealed record ReleaseManifestSignatureMetadata
{
    public required ReleaseManifestSignatureAlgorithm Algorithm { get; init; }
    public required string KeyId { get; init; }
}

internal sealed record ReleaseManifestSigningDocument
{
    public required SignedReleaseManifestPayload Payload { get; init; }
    public required ReleaseManifestSignatureMetadata Signature { get; init; }
}

internal static class SignedReleaseManifestJson
{
    internal const int MaximumManifestBytes = 1024 * 1024;
    internal const int MaximumJsonDepth = 32;

    internal static readonly JsonSerializerOptions Options = CreateOptions();

    internal static SignedReleaseManifestDocument? Deserialize(
        ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<SignedReleaseManifestDocument>(
            utf8Json,
            Options);

    internal static byte[] Serialize(
        SignedReleaseManifestDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, Options);

    internal static byte[] CreateSigningBytes(
        SignedReleaseManifestPayload payload,
        ReleaseManifestSignatureAlgorithm algorithm,
        string keyId) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new ReleaseManifestSigningDocument
            {
                Payload = payload,
                Signature = new ReleaseManifestSignatureMetadata
                {
                    Algorithm = algorithm,
                    KeyId = keyId
                }
            },
            Options);

    internal static bool HasDuplicateProperty(
        ReadOnlyMemory<byte> utf8Json)
    {
        using JsonDocument document = JsonDocument.Parse(
            utf8Json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth
            });
        return HasDuplicateProperty(document.RootElement);
    }

    private static bool HasDuplicateProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) ||
                    HasDuplicateProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (HasDuplicateProperty(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            MaxDepth = MaximumJsonDepth,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}

internal readonly record struct ReleaseSemanticVersion(
    int Major,
    int Minor,
    int Patch,
    string Prerelease,
    string BuildMetadata) : IComparable<ReleaseSemanticVersion>
{
    internal bool IsPrerelease => Prerelease.Length > 0;

    internal static bool TryParse(
        string? value,
        out ReleaseSemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) || value.Length > 96 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        string semantic = value;
        string buildMetadata = string.Empty;
        int buildSeparator = semantic.IndexOf('+', StringComparison.Ordinal);
        if (buildSeparator >= 0)
        {
            if (semantic.IndexOf(
                    "+",
                    buildSeparator + 1,
                    StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            buildMetadata = semantic[(buildSeparator + 1)..];
            semantic = semantic[..buildSeparator];
            if (!ValidateIdentifiers(buildMetadata, allowNumericLeadingZero: true))
            {
                return false;
            }
        }

        string prerelease = string.Empty;
        int prereleaseSeparator = semantic.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseSeparator >= 0)
        {
            prerelease = semantic[(prereleaseSeparator + 1)..];
            semantic = semantic[..prereleaseSeparator];
            if (!ValidateIdentifiers(prerelease, allowNumericLeadingZero: false))
            {
                return false;
            }
        }

        string[] core = semantic.Split('.', StringSplitOptions.None);
        if (core.Length != 3 ||
            !TryParseCore(core[0], out int major) ||
            !TryParseCore(core[1], out int minor) ||
            !TryParseCore(core[2], out int patch))
        {
            return false;
        }

        version = new ReleaseSemanticVersion(
            major,
            minor,
            patch,
            prerelease,
            buildMetadata);
        return true;
    }

    public int CompareTo(ReleaseSemanticVersion other)
    {
        int comparison = Major.CompareTo(other.Major);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0)
        {
            return comparison;
        }

        if (Prerelease.Length == 0)
        {
            return other.Prerelease.Length == 0 ? 0 : 1;
        }
        if (other.Prerelease.Length == 0)
        {
            return -1;
        }

        string[] left = Prerelease.Split('.');
        string[] right = other.Prerelease.Split('.');
        int count = Math.Min(left.Length, right.Length);
        for (int index = 0; index < count; index++)
        {
            bool leftNumeric = IsNumericIdentifier(left[index]);
            bool rightNumeric = IsNumericIdentifier(right[index]);
            if (leftNumeric && rightNumeric)
            {
                comparison = left[index].Length.CompareTo(right[index].Length);
                if (comparison == 0)
                {
                    comparison = string.CompareOrdinal(left[index], right[index]);
                }
            }
            else if (leftNumeric)
            {
                comparison = -1;
            }
            else if (rightNumeric)
            {
                comparison = 1;
            }
            else
            {
                comparison = string.CompareOrdinal(left[index], right[index]);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool TryParseCore(string value, out int parsed)
    {
        parsed = 0;
        if (value.Length == 0 ||
            (value.Length > 1 && value[0] == '0'))
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }
        return int.TryParse(value, out parsed);
    }

    private static bool IsNumericIdentifier(string value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }
        return value.Length > 0;
    }

    private static bool ValidateIdentifiers(
        string value,
        bool allowNumericLeadingZero)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (string identifier in value.Split('.', StringSplitOptions.None))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            bool numeric = true;
            foreach (char character in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return false;
                }
                numeric &= char.IsAsciiDigit(character);
            }

            if (!allowNumericLeadingZero && numeric &&
                identifier.Length > 1 && identifier[0] == '0')
            {
                return false;
            }
        }

        return true;
    }
}
