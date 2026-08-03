using System.Security.Cryptography;
using System.Text.Json;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

public sealed class SignedReleaseManifestVerifier
{
    internal const long MaximumDeclaredPackageLength =
        8L * 1024 * 1024 * 1024;
    private const int MaximumPackageIdentityLength = 96;
    private const int MaximumKeyIdLength = 64;
    private const int MaximumMigrationIdentityLength = 96;
    private const int MaximumReleaseNotesTitleLength = 120;
    private const int MaximumReleaseNotesSummaryLength = 2048;

    private static readonly ReleasePackageRole[] RequiredPackageRoles =
    [
        ReleasePackageRole.GatewayWeb,
        ReleasePackageRole.Broker,
        ReleasePackageRole.AetherRemoteAgent,
        ReleasePackageRole.StationEngine
    ];

    public ReleaseManifestVerificationReport Verify(
        ReadOnlyMemory<byte> manifestUtf8,
        IReadOnlyCollection<LocalImmutableReleasePackage> localPackages,
        ReleaseManifestVerificationContext context,
        IReadOnlyCollection<ReleaseManifestVerificationKey> verificationKeys)
    {
        if (manifestUtf8.IsEmpty ||
            manifestUtf8.Length > SignedReleaseManifestJson.MaximumManifestBytes)
        {
            return Failure(
                ReleaseManifestFailureCode.MalformedManifest,
                "The local release manifest is empty or exceeds the bounded size.");
        }
        if (localPackages is null || context is null || verificationKeys is null)
        {
            return Failure(
                ReleaseManifestFailureCode.MalformedManifest,
                "The local release verification input is incomplete.");
        }

        byte[] immutableManifest = manifestUtf8.ToArray();
        LocalImmutableReleasePackage[] immutablePackages = localPackages.ToArray();
        ReleaseManifestVerificationKey[] immutableVerificationKeys =
            verificationKeys.ToArray();

        SignedReleaseManifestDocument? document;
        try
        {
            if (SignedReleaseManifestJson.HasDuplicateProperty(immutableManifest))
            {
                return Failure(
                    ReleaseManifestFailureCode.MalformedManifest,
                    "The local release manifest contains a duplicate JSON property.");
            }
            document = SignedReleaseManifestJson.Deserialize(immutableManifest);
        }
        catch (JsonException)
        {
            return Failure(
                ReleaseManifestFailureCode.MalformedManifest,
                "The local release manifest is malformed or contains an unknown field.");
        }
        catch (NotSupportedException)
        {
            return Failure(
                ReleaseManifestFailureCode.MalformedManifest,
                "The local release manifest uses an unsupported JSON value.");
        }

        if (document?.Payload is null || document.Signature is null)
        {
            return Failure(
                ReleaseManifestFailureCode.MalformedManifest,
                "The local release manifest is missing its payload or signature metadata.");
        }

        ReleaseManifestVerificationReport? signatureFailure = VerifySignature(
            document.Payload,
            document.Signature,
            immutableVerificationKeys);
        if (signatureFailure is not null)
        {
            return signatureFailure;
        }

        SignedReleaseManifestPayload payload = document.Payload;
        if (payload.SchemaVersion != SignedReleaseManifestPayload.CurrentSchemaVersion)
        {
            return Failure(
                ReleaseManifestFailureCode.UnsupportedManifestSchema,
                "The signed release manifest schema is unsupported.",
                payload);
        }

        if (!TryValidateReleaseIdentity(payload.ReleaseIdentity))
        {
            return Failure(
                ReleaseManifestFailureCode.InvalidReleaseIdentity,
                "The signed release identity is invalid or non-canonical.",
                payload);
        }
        if (!ReleaseSemanticVersion.TryParse(
                payload.Version,
                out ReleaseSemanticVersion targetVersion))
        {
            return Failure(
                ReleaseManifestFailureCode.InvalidSemanticVersion,
                "The signed release semantic version is invalid.",
                payload);
        }

        ReleaseManifestVerificationReport? channelFailure = ValidateChannel(
            payload,
            targetVersion,
            context);
        if (channelFailure is not null)
        {
            return channelFailure;
        }

        if (payload.Architecture is not ReleaseManifestArchitecture.LinuxX64 and
            not ReleaseManifestArchitecture.LinuxArm64 ||
            context.Architecture != payload.Architecture)
        {
            return Failure(
                ReleaseManifestFailureCode.UnsupportedArchitecture,
                "The signed release architecture does not match this local verifier context.",
                payload);
        }

        ReleaseManifestVerificationReport? compatibilityFailure =
            ValidateCompatibility(payload, targetVersion, context);
        if (compatibilityFailure is not null)
        {
            return compatibilityFailure;
        }

        ReleaseManifestVerificationReport? restartFailure =
            ValidateRestartAndMigration(payload, context);
        if (restartFailure is not null)
        {
            return restartFailure;
        }

        ReleaseManifestVerificationReport? txSupportFailure =
            ValidateTxSupport(payload);
        if (txSupportFailure is not null)
        {
            return txSupportFailure;
        }

        ReleaseManifestVerificationReport? releaseNotesFailure =
            ValidateReleaseNotes(payload);
        if (releaseNotesFailure is not null)
        {
            return releaseNotesFailure;
        }

        ReleaseManifestVerificationReport? packageFailure =
            ValidatePackages(payload, immutablePackages);
        return packageFailure ?? ReleaseManifestVerificationReport.Success(payload);
    }

    internal SignedReleaseManifestVerificationResult VerifyDetailed(
        ReadOnlyMemory<byte> manifestUtf8,
        IReadOnlyCollection<LocalImmutableReleasePackage> localPackages,
        ReleaseManifestVerificationContext context,
        IReadOnlyCollection<ReleaseManifestVerificationKey> verificationKeys)
    {
        byte[] immutableManifest = manifestUtf8.ToArray();
        ReleaseManifestVerificationReport report = Verify(
            immutableManifest,
            localPackages,
            context,
            verificationKeys);
        if (!report.Succeeded)
        {
            return SignedReleaseManifestVerificationResult.Failure(report);
        }

        try
        {
            SignedReleaseManifestDocument? document =
                SignedReleaseManifestJson.Deserialize(immutableManifest);
            if (document?.Payload is null)
            {
                return SignedReleaseManifestVerificationResult.Failure(
                    Failure(
                        ReleaseManifestFailureCode.MalformedManifest,
                        "The verified local release manifest payload is unavailable."));
            }

            return SignedReleaseManifestVerificationResult.Success(
                report,
                VerifiedReleaseManifestSnapshot.Create(document.Payload));
        }
        catch (Exception exception)
            when (exception is JsonException or NotSupportedException or
                FormatException or ArgumentException)
        {
            return SignedReleaseManifestVerificationResult.Failure(
                Failure(
                    ReleaseManifestFailureCode.MalformedManifest,
                    "The verified local release manifest could not be retained safely."));
        }
    }

    private static ReleaseManifestVerificationReport? VerifySignature(
        SignedReleaseManifestPayload payload,
        ReleaseManifestSignature signature,
        IReadOnlyCollection<ReleaseManifestVerificationKey> verificationKeys)
    {
        if (signature.Algorithm !=
            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256)
        {
            return Failure(
                ReleaseManifestFailureCode.UnsupportedSignatureAlgorithm,
                "The signed release manifest uses an unsupported signature algorithm.");
        }
        if (!IsBoundedAsciiToken(signature.KeyId, MaximumKeyIdLength))
        {
            return Failure(
                ReleaseManifestFailureCode.UnknownVerificationKey,
                "The signed release manifest does not identify a trusted verification key.");
        }

        ReleaseManifestVerificationKey? matchingKey = null;
        foreach (ReleaseManifestVerificationKey candidate in verificationKeys)
        {
            if (!string.Equals(
                    candidate.KeyId,
                    signature.KeyId,
                    StringComparison.Ordinal) ||
                candidate.Algorithm != signature.Algorithm)
            {
                continue;
            }
            if (matchingKey is not null)
            {
                return Failure(
                    ReleaseManifestFailureCode.InvalidVerificationKey,
                    "The local verification key set is ambiguous.");
            }
            matchingKey = candidate;
        }

        if (matchingKey is null)
        {
            return Failure(
                ReleaseManifestFailureCode.UnknownVerificationKey,
                "The signed release manifest does not identify a trusted verification key.");
        }
        if (!TryDecodeCanonicalBase64Url(signature.Value, out byte[] signatureBytes) ||
            signatureBytes.Length != 64)
        {
            return Failure(
                ReleaseManifestFailureCode.InvalidSignature,
                "The signed release manifest signature is malformed.");
        }

        try
        {
            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(
                matchingKey.SubjectPublicKeyInfo,
                out int bytesRead);
            ECParameters parameters = verifier.ExportParameters(false);
            if (bytesRead != matchingKey.SubjectPublicKeyInfo.Length ||
                verifier.KeySize != 256 ||
                !string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal))
            {
                return Failure(
                    ReleaseManifestFailureCode.InvalidVerificationKey,
                    "The local verification key is invalid for the declared algorithm.");
            }

            byte[] signingBytes = SignedReleaseManifestJson.CreateSigningBytes(
                payload,
                signature.Algorithm,
                signature.KeyId);
            if (!verifier.VerifyData(
                    signingBytes,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return Failure(
                    ReleaseManifestFailureCode.InvalidSignature,
                    "The signed release manifest signature is invalid.");
            }
        }
        catch (CryptographicException)
        {
            return Failure(
                ReleaseManifestFailureCode.InvalidVerificationKey,
                "The local verification key is invalid for the declared algorithm.");
        }
        catch (ArgumentException)
        {
            return Failure(
                ReleaseManifestFailureCode.InvalidVerificationKey,
                "The local verification key is invalid for the declared algorithm.");
        }

        return null;
    }

    private static ReleaseManifestVerificationReport? ValidateChannel(
        SignedReleaseManifestPayload payload,
        ReleaseSemanticVersion targetVersion,
        ReleaseManifestVerificationContext context)
    {
        bool relationshipValid = context.UpdateChannel switch
        {
            InstallationUpdateChannel.Stable =>
                payload.Channel == ReleaseManifestChannel.Stable &&
                string.IsNullOrEmpty(context.PinnedReleaseIdentity) &&
                !targetVersion.IsPrerelease,
            InstallationUpdateChannel.Beta =>
                payload.Channel == ReleaseManifestChannel.Beta &&
                string.IsNullOrEmpty(context.PinnedReleaseIdentity) &&
                targetVersion.IsPrerelease,
            InstallationUpdateChannel.Pinned =>
                payload.Channel == ReleaseManifestChannel.Pinned &&
                TryValidateReleaseIdentity(context.PinnedReleaseIdentity) &&
                string.Equals(
                    payload.ReleaseIdentity,
                    context.PinnedReleaseIdentity,
                    StringComparison.Ordinal),
            _ => false
        };

        return relationshipValid
            ? null
            : Failure(
                ReleaseManifestFailureCode.InvalidChannelRelationship,
                "The signed release channel is incompatible with the exact local update selection.",
                payload);
    }

    private static ReleaseManifestVerificationReport? ValidateCompatibility(
        SignedReleaseManifestPayload payload,
        ReleaseSemanticVersion targetVersion,
        ReleaseManifestVerificationContext context)
    {
        ReleaseConfigurationCompatibility? configuration = payload.Configuration;
        if (configuration is null ||
            configuration.MinimumCompatibleSchemaVersion < 1 ||
            configuration.MaximumCompatibleSchemaVersion <
                configuration.MinimumCompatibleSchemaVersion ||
            configuration.TargetSchemaVersion <
                configuration.MinimumCompatibleSchemaVersion ||
            configuration.TargetSchemaVersion >
                configuration.MaximumCompatibleSchemaVersion ||
            context.ConfigurationSchemaVersion <
                configuration.MinimumCompatibleSchemaVersion ||
            context.ConfigurationSchemaVersion >
                configuration.MaximumCompatibleSchemaVersion)
        {
            return Failure(
                ReleaseManifestFailureCode.IncompatibleConfigurationSchema,
                "The signed release is incompatible with the local configuration schema.",
                payload);
        }

        ReleaseProtocolCompatibility? protocol = payload.Protocol;
        if (protocol is null || protocol.MinimumVersion < 1 ||
            protocol.MaximumVersion < protocol.MinimumVersion ||
            context.ProtocolVersion < protocol.MinimumVersion ||
            context.ProtocolVersion > protocol.MaximumVersion)
        {
            return Failure(
                ReleaseManifestFailureCode.IncompatibleProtocolVersion,
                "The signed release is incompatible with the local protocol version.",
                payload);
        }

        if (!ReleaseSemanticVersion.TryParse(
                payload.MinimumPreviousVersion,
                out ReleaseSemanticVersion minimumPreviousVersion))
        {
            return Failure(
                ReleaseManifestFailureCode.UnsupportedPreviousVersionTransition,
                "The signed release minimum previous version is invalid.",
                payload);
        }

        if (!string.IsNullOrEmpty(context.InstalledVersion))
        {
            if (!ReleaseSemanticVersion.TryParse(
                    context.InstalledVersion,
                    out ReleaseSemanticVersion installedVersion) ||
                installedVersion.CompareTo(minimumPreviousVersion) < 0 ||
                installedVersion.CompareTo(targetVersion) >= 0)
            {
                return Failure(
                    ReleaseManifestFailureCode.UnsupportedPreviousVersionTransition,
                    "The signed release does not support the local previous-version transition.",
                    payload);
            }
        }

        return null;
    }

    private static ReleaseManifestVerificationReport? ValidateRestartAndMigration(
        SignedReleaseManifestPayload payload,
        ReleaseManifestVerificationContext context)
    {
        ReleaseRestartDeclaration? restart = payload.Restart;
        if (restart is null ||
            restart.Host &&
            (!restart.GatewayWeb || !restart.Broker ||
             !restart.AetherRemoteAgent || !restart.StationEngine))
        {
            return Failure(
                ReleaseManifestFailureCode.ContradictoryRestartDeclaration,
                "The signed release restart declaration is contradictory.",
                payload);
        }

        ReleaseMigrationDeclaration? migration = payload.Migration;
        ReleaseConfigurationCompatibility configuration = payload.Configuration;
        if (migration is null)
        {
            return Failure(
                ReleaseManifestFailureCode.InvalidMigrationDeclaration,
                "The signed release migration declaration is missing.",
                payload);
        }

        switch (migration.Kind)
        {
            case ReleaseMigrationKind.None:
                if (migration.FromConfigurationSchemaVersion is not null ||
                    migration.ToConfigurationSchemaVersion is not null ||
                    !string.IsNullOrEmpty(migration.MigrationIdentity) ||
                    context.ConfigurationSchemaVersion !=
                        configuration.TargetSchemaVersion)
                {
                    return Failure(
                        ReleaseManifestFailureCode.InvalidMigrationDeclaration,
                        "The signed release migration declaration contradicts the configuration transition.",
                        payload);
                }
                break;
            case ReleaseMigrationKind.Required:
                if (migration.FromConfigurationSchemaVersion is null ||
                    migration.ToConfigurationSchemaVersion is null ||
                    migration.FromConfigurationSchemaVersion.Value !=
                        context.ConfigurationSchemaVersion ||
                    migration.ToConfigurationSchemaVersion.Value !=
                        configuration.TargetSchemaVersion ||
                    migration.FromConfigurationSchemaVersion.Value >=
                        migration.ToConfigurationSchemaVersion.Value ||
                    !IsBoundedAsciiToken(
                        migration.MigrationIdentity,
                        MaximumMigrationIdentityLength) ||
                    !restart.GatewayWeb)
                {
                    return Failure(
                        ReleaseManifestFailureCode.InvalidMigrationDeclaration,
                        "The signed release migration declaration contradicts the configuration transition.",
                        payload);
                }
                break;
            default:
                return Failure(
                    ReleaseManifestFailureCode.InvalidMigrationDeclaration,
                    "The signed release migration declaration is unsupported.",
                    payload);
        }

        return null;
    }

    private static ReleaseManifestVerificationReport? ValidateTxSupport(
        SignedReleaseManifestPayload payload)
    {
        ReleaseTxSupportDeclaration? declaration = payload.TxSupport;
        if (declaration is null ||
            declaration.DeclarationVersion !=
                ReleaseTxSupportDeclaration.CurrentDeclarationVersion ||
            declaration.Capability is not ReleaseTxSupportCapability.None and
                not ReleaseTxSupportCapability.Available ||
            declaration.EnablesTransmit ||
            declaration.GrantsTransmitEligibility ||
            declaration.CreatesBrowserTransmitAuthority ||
            declaration.ArmsWatchdog)
        {
            return Failure(
                ReleaseManifestFailureCode.InvalidTxSupportDeclaration,
                "The signed release TX-support declaration is malformed or grants forbidden authority.",
                payload);
        }

        return null;
    }

    private static ReleaseManifestVerificationReport? ValidateReleaseNotes(
        SignedReleaseManifestPayload payload)
    {
        ReleaseNotesMetadata? notes = payload.ReleaseNotes;
        if (notes is null ||
            !IsBoundedText(
                notes.Title,
                minimumLength: 1,
                MaximumReleaseNotesTitleLength) ||
            !IsBoundedText(
                notes.Summary,
                minimumLength: 0,
                MaximumReleaseNotesSummaryLength))
        {
            return Failure(
                ReleaseManifestFailureCode.InvalidReleaseNotes,
                "The signed release-note metadata is missing or exceeds its bounds.",
                payload);
        }

        return null;
    }

    private static ReleaseManifestVerificationReport? ValidatePackages(
        SignedReleaseManifestPayload payload,
        IReadOnlyCollection<LocalImmutableReleasePackage> localPackages)
    {
        if (payload.Packages is null)
        {
            return Failure(
                ReleaseManifestFailureCode.InvalidPackageDeclaration,
                "The signed release package declaration is missing.",
                payload);
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        HashSet<string> paths = new(StringComparer.Ordinal);
        HashSet<ReleasePackageRole> roles = [];
        Dictionary<string, SignedReleasePackage> declaredByPath =
            new(StringComparer.Ordinal);

        foreach (SignedReleasePackage package in payload.Packages)
        {
            if (package is null ||
                !IsBoundedAsciiToken(
                    package.PackageIdentity,
                    MaximumPackageIdentityLength) ||
                package.Length <= 0 ||
                package.Length > MaximumDeclaredPackageLength ||
                !TryParseCanonicalSha256(package.Sha256, out _))
            {
                return Failure(
                    ReleaseManifestFailureCode.InvalidPackageDeclaration,
                    "A signed release package declaration is malformed.",
                    payload);
            }
            if (!identities.Add(package.PackageIdentity))
            {
                return Failure(
                    ReleaseManifestFailureCode.DuplicatePackageIdentity,
                    "The signed release contains a duplicate package identity.",
                    payload);
            }
            if (!ReleasePackagePath.IsSafe(package.FileName))
            {
                return Failure(
                    ReleaseManifestFailureCode.InvalidPackagePath,
                    "The signed release contains an unsafe package path.",
                    payload);
            }
            if (!paths.Add(package.FileName))
            {
                return Failure(
                    ReleaseManifestFailureCode.DuplicatePackagePath,
                    "The signed release contains a duplicate package path.",
                    payload);
            }
            if (!RequiredPackageRoles.Contains(package.Role))
            {
                return Failure(
                    ReleaseManifestFailureCode.UnexpectedPackageRole,
                    "The signed release contains an unexpected package role.",
                    payload);
            }
            if (!roles.Add(package.Role))
            {
                return Failure(
                    ReleaseManifestFailureCode.DuplicatePackageRole,
                    "The signed release contains more than one package for a required role.",
                    payload);
            }
            declaredByPath.Add(package.FileName, package);
        }

        foreach (ReleasePackageRole requiredRole in RequiredPackageRoles)
        {
            if (!roles.Contains(requiredRole))
            {
                return Failure(
                    ReleaseManifestFailureCode.MissingPackageRole,
                    "The signed release is missing a required package role.",
                    payload);
            }
        }

        Dictionary<string, LocalImmutableReleasePackage> localByPath =
            new(StringComparer.Ordinal);
        foreach (LocalImmutableReleasePackage package in localPackages)
        {
            if (package is null ||
                !ReleasePackagePath.IsSafe(package.RelativePath))
            {
                return Failure(
                    ReleaseManifestFailureCode.InvalidPackagePath,
                    "A local release package input has an unsafe path.",
                    payload);
            }
            if (!localByPath.TryAdd(package.RelativePath, package))
            {
                return Failure(
                    ReleaseManifestFailureCode.DuplicatePackagePath,
                    "The local release package inputs contain a duplicate path.",
                    payload);
            }
        }

        foreach ((string path, SignedReleasePackage declared) in declaredByPath)
        {
            if (!localByPath.TryGetValue(path, out LocalImmutableReleasePackage? local))
            {
                return Failure(
                    ReleaseManifestFailureCode.MissingPackageInput,
                    "A package declared by the signed release is missing locally.",
                    payload);
            }
            if (local.Length != declared.Length)
            {
                return Failure(
                    ReleaseManifestFailureCode.PackageSizeMismatch,
                    "A local release package length does not match the signed declaration.",
                    payload);
            }

            _ = TryParseCanonicalSha256(declared.Sha256, out byte[] expectedHash);
            if (!CryptographicOperations.FixedTimeEquals(
                    local.Sha256,
                    expectedHash))
            {
                return Failure(
                    ReleaseManifestFailureCode.PackageSha256Mismatch,
                    "A local release package SHA-256 does not match the signed declaration.",
                    payload);
            }
        }

        if (localByPath.Count != declaredByPath.Count)
        {
            return Failure(
                ReleaseManifestFailureCode.UnexpectedPackageInput,
                "The local release package set contains an undeclared package.",
                payload);
        }

        return null;
    }

    private static bool TryValidateReleaseIdentity(string? value)
    {
        try
        {
            string parsed = InstallationReleaseIdentity.Parse(value);
            return string.Equals(parsed, value, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsBoundedAsciiToken(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength ||
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

    private static bool TryParseCanonicalSha256(
        string? value,
        out byte[] hash)
    {
        hash = [];
        if (value is null || value.Length != 64)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character) &&
                character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        try
        {
            hash = Convert.FromHexString(value);
            return hash.Length == 32;
        }
        catch (FormatException)
        {
            hash = [];
            return false;
        }
    }

    private static bool TryDecodeCanonicalBase64Url(
        string? value,
        out byte[] decoded)
    {
        decoded = [];
        if (string.IsNullOrEmpty(value) || value.Length > 512)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_')
            {
                return false;
            }
        }

        int paddingLength = (4 - value.Length % 4) % 4;
        string padded = value.Replace('-', '+').Replace('_', '/') +
            new string('=', paddingLength);
        try
        {
            decoded = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        string canonical = Convert.ToBase64String(decoded)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return string.Equals(canonical, value, StringComparison.Ordinal);
    }

    private static bool IsBoundedText(
        string? value,
        int minimumLength,
        int maximumLength)
    {
        if (value is null || value.Length < minimumLength ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }
        foreach (char character in value)
        {
            if (char.IsControl(character) && character is not '\n' and not '\r' and not '\t')
            {
                return false;
            }
        }
        return true;
    }

    private static ReleaseManifestVerificationReport Failure(
        ReleaseManifestFailureCode failureCode,
        string message,
        SignedReleaseManifestPayload? trustedPayload = null) =>
        ReleaseManifestVerificationReport.Failure(
            failureCode,
            message,
            trustedPayload);
}
