using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class GitHubReleaseSourceSettings
{
    public const string SectionName = "ReleaseGitHubSource";

    public bool Enabled { get; set; }
    public string Owner { get; set; } = "KingSteve032";
    public string Repository { get; set; } = "AetherSDR-Web";
    public int MaximumReleaseCount { get; set; } = 32;
    public int RequestTimeoutSeconds { get; set; } = 30;
}

public enum GitHubReleaseBundleFailureCode
{
    None = 0,
    SourceDisabled = 1,
    VerificationTrustUnavailable = 2,
    SourceUnavailable = 3,
    InvalidReleaseMetadata = 4,
    NoEligibleRelease = 5,
    MissingReleaseAsset = 6,
    DuplicateReleaseAsset = 7,
    UnsafeReleaseAsset = 8,
    ReleaseAssetTooLarge = 9,
    ReleaseAssetChanged = 10,
    DownloadFailed = 11,
    TemporaryBundleFailed = 12,
    VerificationFailed = 13,
    ReleaseIdentityMismatch = 14,
    CleanupFailed = 15
}

public sealed record GitHubReleaseBundleSourceDiagnostics(
    bool Registered,
    bool Enabled,
    bool RepositoryConfigured,
    bool GitHubMetadataReadRegistered,
    bool GitHubAssetDownloadRegistered,
    bool TemporaryBundleWriteRegistered,
    bool TemporaryBundleCleanupRegistered,
    bool LocalSignedVerificationRegistered,
    int MaximumReleaseCount,
    int RequestTimeoutSeconds,
    bool PersistentDownloadRegistered,
    bool ArchiveExtractionRegistered,
    bool StagingRegistered,
    bool InstallationRegistered,
    bool ActivationRegistered,
    bool RollbackRegistered,
    bool MigrationRegistered,
    bool ServiceControlRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered,
    string Reason);

public sealed record GitHubReleaseBundleCheckConsoleDiagnostics(
    bool Registered,
    bool GitHubSourceRegistered,
    bool NetworkReadRegistered,
    bool TemporaryBundleRegistered,
    bool LocalSignedVerificationRegistered,
    bool PersistentDownloadRegistered,
    bool ArchiveExtractionRegistered,
    bool StagingRegistered,
    bool InstallationRegistered,
    bool ActivationRegistered,
    bool RollbackRegistered,
    bool MigrationRegistered,
    bool ServiceControlRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

public sealed record GitHubReleaseBundleCheckReport(
    int ReportVersion,
    string Command,
    bool Succeeded,
    int ExitCode,
    GitHubReleaseBundleFailureCode FailureCode,
    string Message,
    int ExaminedReleaseCount,
    int DownloadedAssetCount,
    long DownloadedBytes,
    ReleaseManifestVerificationReport? Verification);

internal sealed record GitHubReleaseBundleCheckResult(
    GitHubReleaseBundleFailureCode FailureCode,
    string Message,
    int ExaminedReleaseCount,
    int DownloadedAssetCount,
    long DownloadedBytes,
    ReleaseManifestVerificationReport? Verification)
{
    internal bool Succeeded => FailureCode == GitHubReleaseBundleFailureCode.None;

    internal static GitHubReleaseBundleCheckResult Success(
        int examinedReleaseCount,
        int downloadedAssetCount,
        long downloadedBytes,
        ReleaseManifestVerificationReport verification) =>
        new(
            GitHubReleaseBundleFailureCode.None,
            "The selected GitHub release bundle verified successfully.",
            examinedReleaseCount,
            downloadedAssetCount,
            downloadedBytes,
            verification);

    internal static GitHubReleaseBundleCheckResult Failure(
        GitHubReleaseBundleFailureCode failureCode,
        string message,
        int examinedReleaseCount = 0,
        int downloadedAssetCount = 0,
        long downloadedBytes = 0,
        ReleaseManifestVerificationReport? verification = null) =>
        new(
            failureCode,
            message,
            examinedReleaseCount,
            downloadedAssetCount,
            downloadedBytes,
            verification);
}

internal sealed record GitHubReleaseBundleAcquisition(
    GitHubReleaseBundleCheckResult Result,
    string BundleDirectory)
{
    internal bool Succeeded =>
        Result.Succeeded && BundleDirectory.Length > 0;
}

internal static class GitHubReleaseHttpClient
{
    internal const string ClientName = "aethersdr-release-github";
    internal const string ApiVersion = "2026-03-10";

    internal static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AetherSDR-Web", "1.0"));
        client.DefaultRequestHeaders.Remove("X-GitHub-Api-Version");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-GitHub-Api-Version",
            ApiVersion);
    }

    internal static HttpMessageHandler CreateHandler() =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxAutomaticRedirections = 0,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

    internal static HttpClient CreateStandaloneClient()
    {
        HttpClient client = new(CreateHandler(), disposeHandler: true);
        Configure(client);
        return client;
    }
}

/// <summary>
/// Disabled-by-default GitHub release reader for the configured public repository.
/// It downloads one exact architecture manifest and four exact package assets into
/// a private temporary directory, freezes the directory, delegates all trust and
/// compatibility decisions to the existing local signed-bundle verifier, and then
/// removes the temporary directory. It has no persistent download, extraction,
/// staging, installation, activation, rollback, service-control, Admin, browser,
/// radio, watchdog, command, lease, or transmit authority.
/// </summary>
public sealed class GitHubReleaseBundleSource
{
    internal const int MaximumMetadataBytes = 2 * 1024 * 1024;
    internal const int MaximumReleaseAssets = 64;
    internal const int MaximumRedirects = 4;

    private const string ApiHost = "api.github.com";
    private static readonly string[] AllowedDownloadHosts =
    [
        ApiHost,
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    ];

    private readonly ValidatedSettings m_settings;
    private readonly Func<HttpClient> m_clientFactory;
    private readonly LocalOfflineReleaseBundleVerificationService
        m_bundleVerificationService;
    private readonly string m_temporaryRoot;

    public GitHubReleaseBundleSource(
        IOptions<GitHubReleaseSourceSettings> options,
        IHttpClientFactory httpClientFactory,
        LocalOfflineReleaseBundleVerificationService bundleVerificationService,
        ILogger<GitHubReleaseBundleSource> logger)
        : this(
            ValidateSettings(options?.Value ?? new GitHubReleaseSourceSettings()),
            () => httpClientFactory?.CreateClient(GitHubReleaseHttpClient.ClientName) ??
                throw new ArgumentNullException(nameof(httpClientFactory)),
            bundleVerificationService,
            Path.GetTempPath(),
            logger)
    {
    }

    internal GitHubReleaseBundleSource(
        GitHubReleaseSourceSettings settings,
        Func<HttpClient> clientFactory,
        LocalOfflineReleaseBundleVerificationService bundleVerificationService,
        string temporaryRoot,
        ILogger<GitHubReleaseBundleSource>? logger = null)
        : this(
            ValidateSettings(settings),
            clientFactory,
            bundleVerificationService,
            temporaryRoot,
            logger)
    {
    }

    private GitHubReleaseBundleSource(
        ValidatedSettings settings,
        Func<HttpClient> clientFactory,
        LocalOfflineReleaseBundleVerificationService bundleVerificationService,
        string temporaryRoot,
        ILogger<GitHubReleaseBundleSource>? logger)
    {
        m_settings = settings;
        m_clientFactory = clientFactory ??
            throw new ArgumentNullException(nameof(clientFactory));
        m_bundleVerificationService = bundleVerificationService ??
            throw new ArgumentNullException(nameof(bundleVerificationService));
        m_temporaryRoot = ValidateTemporaryRoot(temporaryRoot);
        Snapshot = new GitHubReleaseBundleSourceDiagnostics(
            Registered: true,
            Enabled: settings.Enabled,
            RepositoryConfigured: true,
            GitHubMetadataReadRegistered: true,
            GitHubAssetDownloadRegistered: true,
            TemporaryBundleWriteRegistered: true,
            TemporaryBundleCleanupRegistered: true,
            LocalSignedVerificationRegistered: true,
            settings.MaximumReleaseCount,
            settings.RequestTimeoutSeconds,
            PersistentDownloadRegistered: false,
            ArchiveExtractionRegistered: false,
            StagingRegistered: false,
            InstallationRegistered: false,
            ActivationRegistered: false,
            RollbackRegistered: false,
            MigrationRegistered: false,
            ServiceControlRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false,
            Reason: settings.Enabled ? "ready" : "disabled");

        logger?.LogInformation(
            "GitHub release bundle checking is {State}; metadata and exact asset " +
            "downloads feed only the local signed verifier, while persistent " +
            "download, extraction, installation, activation, radio, command, " +
            "lease, and TX callers remain absent",
            Snapshot.Enabled ? "enabled" : "disabled");
    }

    public GitHubReleaseBundleSourceDiagnostics Snapshot { get; }

    internal async Task<GitHubReleaseBundleCheckResult> CheckAsync(
        ReleaseManifestVerificationContext context,
        CancellationToken cancellationToken = default)
    {
        GitHubReleaseBundleAcquisition acquisition =
            await AcquireVerifiedBundleAsync(
                context,
                cancellationToken).ConfigureAwait(false);
        if (!acquisition.Succeeded)
        {
            return acquisition.Result;
        }
        if (!TryDeleteTemporaryBundle(acquisition.BundleDirectory))
        {
            return GitHubReleaseBundleCheckResult.Failure(
                GitHubReleaseBundleFailureCode.CleanupFailed,
                "The temporary GitHub release bundle could not be removed safely.",
                acquisition.Result.ExaminedReleaseCount,
                acquisition.Result.DownloadedAssetCount,
                acquisition.Result.DownloadedBytes,
                acquisition.Result.Verification);
        }
        return acquisition.Result;
    }

    internal async Task<GitHubReleaseBundleAcquisition>
        AcquireVerifiedBundleAsync(
            ReleaseManifestVerificationContext context,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!m_settings.Enabled)
        {
            return new GitHubReleaseBundleAcquisition(
                GitHubReleaseBundleCheckResult.Failure(
                    GitHubReleaseBundleFailureCode.SourceDisabled,
                    "GitHub release bundle checking is disabled."),
                string.Empty);
        }
        if (!m_bundleVerificationService.LocalVerificationAvailable)
        {
            return new GitHubReleaseBundleAcquisition(
                GitHubReleaseBundleCheckResult.Failure(
                    GitHubReleaseBundleFailureCode.VerificationTrustUnavailable,
                    "GitHub release assets cannot be read because signed release verification trust is unavailable."),
                string.Empty);
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(m_settings.RequestTimeoutSeconds));

        string? temporaryDirectory = null;
        GitHubReleaseBundleCheckResult result;
        try
        {
            using HttpClient client = m_clientFactory();
            GitHubReleaseHttpClient.Configure(client);
            GitHubRelease[] releases = await ReadReleasesAsync(
                client,
                timeout.Token).ConfigureAwait(false);
            GitHubRelease candidate = SelectCandidate(releases, context);
            RequiredAssets assets = SelectRequiredAssets(candidate, context.Architecture);

            temporaryDirectory = CreateTemporaryBundleDirectory();
            int downloadedAssetCount = 0;
            long downloadedBytes = 0;

            DownloadedAsset manifest = await DownloadAssetAsync(
                client,
                assets.Manifest,
                Path.Combine(
                    temporaryDirectory,
                    LocalOfflineReleaseBundleVerificationService.ManifestFileName),
                SignedReleaseManifestJson.MaximumManifestBytes,
                timeout.Token).ConfigureAwait(false);
            downloadedAssetCount++;
            downloadedBytes = checked(downloadedBytes + manifest.Length);

            string packagesDirectory = Path.Combine(temporaryDirectory, "packages");
            CreatePrivateDirectory(packagesDirectory);
            foreach (GitHubReleaseAsset package in assets.Packages)
            {
                DownloadedAsset downloaded = await DownloadAssetAsync(
                    client,
                    package,
                    Path.Combine(packagesDirectory, package.Name),
                    SignedReleaseManifestVerifier.MaximumDeclaredPackageLength,
                    timeout.Token).ConfigureAwait(false);
                downloadedAssetCount++;
                downloadedBytes = checked(downloadedBytes + downloaded.Length);
            }

            FreezeBundle(temporaryDirectory);
            LocalOfflineReleaseBundleVerificationReport verification =
                m_bundleVerificationService.VerifyDirectory(
                    temporaryDirectory,
                    context);
            if (!verification.Succeeded || verification.Verification is null)
            {
                result = GitHubReleaseBundleCheckResult.Failure(
                    GitHubReleaseBundleFailureCode.VerificationFailed,
                    "The downloaded GitHub release bundle failed signed verification.",
                    releases.Length,
                    downloadedAssetCount,
                    downloadedBytes,
                    verification.Verification);
            }
            else if (!string.Equals(
                         verification.Verification.ReleaseIdentity,
                         candidate.TagName,
                         StringComparison.Ordinal))
            {
                result = GitHubReleaseBundleCheckResult.Failure(
                    GitHubReleaseBundleFailureCode.ReleaseIdentityMismatch,
                    "The signed release identity does not match the selected GitHub release tag.",
                    releases.Length,
                    downloadedAssetCount,
                    downloadedBytes,
                    verification.Verification);
            }
            else
            {
                result = GitHubReleaseBundleCheckResult.Success(
                    releases.Length,
                    downloadedAssetCount,
                    downloadedBytes,
                    verification.Verification);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = GitHubReleaseBundleCheckResult.Failure(
                GitHubReleaseBundleFailureCode.SourceUnavailable,
                "The GitHub release check exceeded its bounded request timeout.");
        }
        catch (OperationCanceledException)
        {
            if (temporaryDirectory is not null)
            {
                _ = TryDeleteTemporaryBundle(temporaryDirectory);
            }
            throw;
        }
        catch (GitHubReleaseException exception)
        {
            result = GitHubReleaseBundleCheckResult.Failure(
                exception.FailureCode,
                exception.Message,
                exception.ExaminedReleaseCount,
                exception.DownloadedAssetCount,
                exception.DownloadedBytes);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException or JsonException or
                SecurityException or UnauthorizedAccessException or
                CryptographicException or ArgumentException or NotSupportedException or
                PathTooLongException or OverflowException)
        {
            result = GitHubReleaseBundleCheckResult.Failure(
                GitHubReleaseBundleFailureCode.SourceUnavailable,
                "The GitHub release bundle could not be checked safely.");
        }

        if (result.Succeeded && temporaryDirectory is not null)
        {
            return new GitHubReleaseBundleAcquisition(
                result,
                temporaryDirectory);
        }
        if (temporaryDirectory is not null &&
            !TryDeleteTemporaryBundle(temporaryDirectory))
        {
            result = GitHubReleaseBundleCheckResult.Failure(
                GitHubReleaseBundleFailureCode.CleanupFailed,
                "The temporary GitHub release bundle could not be removed safely.",
                result.ExaminedReleaseCount,
                result.DownloadedAssetCount,
                result.DownloadedBytes,
                result.Verification);
        }
        return new GitHubReleaseBundleAcquisition(result, string.Empty);
    }

    internal static bool TryDeleteAcquiredBundle(string path) =>
        TryDeleteTemporaryBundle(path);

    private async Task<GitHubRelease[]> ReadReleasesAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        Uri requestUri = new(
            $"https://{ApiHost}/repos/{m_settings.Owner}/{m_settings.Repository}" +
            $"/releases?per_page={m_settings.MaximumReleaseCount}");
        using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (IsRedirect(response.StatusCode))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.UnsafeReleaseAsset,
                "GitHub release metadata unexpectedly redirected.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.SourceUnavailable,
                "GitHub release metadata is unavailable.");
        }

        byte[] payload = await ReadBoundedContentAsync(
            response.Content,
            MaximumMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        return ParseReleases(payload);
    }

    private GitHubRelease[] ParseReleases(ReadOnlySpan<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(
            payload.ToArray(),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub release metadata must be a JSON array.");
        }

        int count = document.RootElement.GetArrayLength();
        if (count > m_settings.MaximumReleaseCount)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub returned more releases than the configured bound.");
        }

        List<GitHubRelease> releases = [];
        foreach (JsonElement releaseElement in document.RootElement.EnumerateArray())
        {
            if (releaseElement.ValueKind != JsonValueKind.Object)
            {
                throw Failure(
                    GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                    "GitHub release metadata contains a non-object release.");
            }

            string tagName = RequiredString(releaseElement, "tag_name", 96);
            bool draft = RequiredBoolean(releaseElement, "draft");
            bool prerelease = RequiredBoolean(releaseElement, "prerelease");
            JsonElement assetsElement = RequiredProperty(releaseElement, "assets");
            if (assetsElement.ValueKind != JsonValueKind.Array ||
                assetsElement.GetArrayLength() > MaximumReleaseAssets)
            {
                throw Failure(
                    GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                    "GitHub release assets are malformed or exceed their bound.");
            }

            List<GitHubReleaseAsset> assets = [];
            foreach (JsonElement assetElement in assetsElement.EnumerateArray())
            {
                assets.Add(ParseAsset(assetElement));
            }
            releases.Add(new GitHubRelease(tagName, draft, prerelease, [.. assets]));
        }
        return [.. releases];
    }

    private GitHubReleaseAsset ParseAsset(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub release metadata contains a non-object asset.");
        }

        long id = RequiredPositiveInt64(element, "id");
        string name = RequiredString(element, "name", ReleasePackagePath.MaximumLength);
        if (!IsSafeAssetName(name))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.UnsafeReleaseAsset,
                "A GitHub release asset has an unsafe name.");
        }
        string state = RequiredString(element, "state", 32);
        if (!string.Equals(state, "uploaded", StringComparison.Ordinal))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "A required GitHub release asset is not fully uploaded.");
        }
        long size = RequiredPositiveInt64(element, "size");
        if (size > SignedReleaseManifestVerifier.MaximumDeclaredPackageLength)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.ReleaseAssetTooLarge,
                "A GitHub release asset exceeds the package-size bound.");
        }
        string url = RequiredString(element, "url", 1024);
        Uri apiUri = ValidateAssetApiUri(url, id);
        byte[]? digest = OptionalSha256Digest(element);
        return new GitHubReleaseAsset(id, name, size, apiUri, digest);
    }

    private GitHubRelease SelectCandidate(
        IReadOnlyList<GitHubRelease> releases,
        ReleaseManifestVerificationContext context)
    {
        List<(GitHubRelease Release, ReleaseSemanticVersion Version)> eligible = [];
        foreach (GitHubRelease release in releases)
        {
            if (release.Draft ||
                !TryParseReleaseTag(release.TagName, out ReleaseSemanticVersion version))
            {
                continue;
            }

            bool channelEligible = context.UpdateChannel switch
            {
                InstallationUpdateChannel.Stable =>
                    !release.Prerelease && !version.IsPrerelease,
                InstallationUpdateChannel.Beta =>
                    release.Prerelease && version.IsPrerelease,
                InstallationUpdateChannel.Pinned =>
                    string.Equals(
                        release.TagName,
                        context.PinnedReleaseIdentity,
                        StringComparison.Ordinal),
                _ => false
            };
            if (channelEligible)
            {
                eligible.Add((release, version));
            }
        }

        if (eligible.Count == 0)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.NoEligibleRelease,
                "No GitHub release matches the exact local update channel selection.",
                releases.Count);
        }

        eligible.Sort((left, right) => right.Version.CompareTo(left.Version));
        if (eligible.Count > 1 &&
            eligible[0].Version.CompareTo(eligible[1].Version) == 0 &&
            !string.Equals(
                eligible[0].Release.TagName,
                eligible[1].Release.TagName,
                StringComparison.Ordinal))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub contains ambiguous releases with equal semantic precedence.",
                releases.Count);
        }
        return eligible[0].Release;
    }

    private RequiredAssets SelectRequiredAssets(
        GitHubRelease release,
        ReleaseManifestArchitecture architecture)
    {
        string architectureName = architecture switch
        {
            ReleaseManifestArchitecture.LinuxX64 => "linux-x64",
            ReleaseManifestArchitecture.LinuxArm64 => "linux-arm64",
            _ => throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub release checking supports only linux-x64 and linux-arm64.")
        };
        string manifestName = $"release-manifest-{architectureName}.json";
        string[] packageNames =
        [
            $"aethersdr-gateway-{architectureName}.tar.gz",
            $"aethersdr-broker-{architectureName}.tar.gz",
            $"aetherremote-agent-{architectureName}.tar.gz",
            $"aethersdr-station-engine-{architectureName}.tar.gz"
        ];

        GitHubReleaseAsset manifest = SelectExactAsset(release, manifestName);
        if (manifest.Size > SignedReleaseManifestJson.MaximumManifestBytes)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.ReleaseAssetTooLarge,
                "The GitHub release manifest asset exceeds its bound.");
        }
        GitHubReleaseAsset[] packages = packageNames
            .Select(name => SelectExactAsset(release, name))
            .ToArray();
        return new RequiredAssets(manifest, packages);
    }

    private static GitHubReleaseAsset SelectExactAsset(
        GitHubRelease release,
        string name)
    {
        GitHubReleaseAsset[] matches = release.Assets
            .Where(asset => string.Equals(asset.Name, name, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.MissingReleaseAsset,
                "The selected GitHub release is missing a required architecture asset.");
        }
        if (matches.Length != 1)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.DuplicateReleaseAsset,
                "The selected GitHub release contains duplicate required assets.");
        }
        return matches[0];
    }

    private async Task<DownloadedAsset> DownloadAssetAsync(
        HttpClient client,
        GitHubReleaseAsset asset,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (asset.Size <= 0 || asset.Size > maximumBytes)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.ReleaseAssetTooLarge,
                "A required GitHub release asset is empty or exceeds its bound.");
        }

        using HttpResponseMessage response = await SendAssetRequestAsync(
            client,
            asset.ApiUri,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.DownloadFailed,
                "A required GitHub release asset could not be downloaded.");
        }
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength is not null && contentLength != asset.Size)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.ReleaseAssetChanged,
                "A GitHub release asset length changed between metadata and download.");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            await using Stream input = await response.Content.ReadAsStreamAsync(
                cancellationToken).ConfigureAwait(false);
            await using FileStream output = new(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous |
                    FileOptions.SequentialScan |
                    FileOptions.WriteThrough);
            while (true)
            {
                int read = await input.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                total = checked(total + read);
                if (total > asset.Size || total > maximumBytes)
                {
                    throw Failure(
                        GitHubReleaseBundleFailureCode.ReleaseAssetChanged,
                        "A GitHub release asset exceeded its declared length while downloading.");
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (total != asset.Size)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.ReleaseAssetChanged,
                "A GitHub release asset was truncated while downloading.");
        }
        byte[] downloadedDigest = hash.GetHashAndReset();
        try
        {
            if (asset.Sha256 is not null &&
                !CryptographicOperations.FixedTimeEquals(
                    downloadedDigest,
                    asset.Sha256))
            {
                throw Failure(
                    GitHubReleaseBundleFailureCode.ReleaseAssetChanged,
                    "A GitHub release asset digest changed between metadata and download.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(downloadedDigest);
        }

        FreezeFile(destinationPath);
        return new DownloadedAsset(total);
    }

    private static async Task<HttpResponseMessage> SendAssetRequestAsync(
        HttpClient client,
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri current = initialUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                ValidateAllowedDownloadUri(
                    response.RequestMessage?.RequestUri ?? current);
                return response;
            }

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw Failure(
                    GitHubReleaseBundleFailureCode.UnsafeReleaseAsset,
                    "A GitHub asset redirect omitted its destination.");
            }
            current = location.IsAbsoluteUri
                ? location
                : new Uri(current, location);
            ValidateAllowedDownloadUri(current);
        }

        throw Failure(
            GitHubReleaseBundleFailureCode.UnsafeReleaseAsset,
            "A GitHub asset exceeded the redirect bound.");
    }

    private static async Task<byte[]> ReadBoundedContentAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        long? declared = content.Headers.ContentLength;
        if (declared.HasValue && declared.Value > maximumBytes)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub release metadata exceeds its byte bound.");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            await using Stream stream = await content.ReadAsStreamAsync(
                cancellationToken).ConfigureAwait(false);
            using MemoryStream output = new();
            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (output.Length + read > maximumBytes)
                {
                    throw Failure(
                        GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                        "GitHub release metadata exceeds its byte bound.");
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private string CreateTemporaryBundleDirectory()
    {
        if (!Directory.Exists(m_temporaryRoot))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.TemporaryBundleFailed,
                "The configured temporary root is unavailable.");
        }
        string identity = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string path = Path.Combine(
            m_temporaryRoot,
            $"aethersdr-github-release-{identity}");
        try
        {
            CreatePrivateDirectory(path);
            return path;
        }
        catch
        {
            _ = TryDeleteTemporaryBundle(path);
            throw;
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    private static void FreezeBundle(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string file in Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            }
            return;
        }

        foreach (string file in Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories))
        {
            FreezeFile(file);
        }
        foreach (string directory in Directory.EnumerateDirectories(
            root,
            "*",
            SearchOption.AllDirectories)
            .OrderByDescending(value => value.Length))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead |
                UnixFileMode.UserExecute);
        }
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead |
            UnixFileMode.UserExecute);
    }

    private static void FreezeFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead);
        }
    }

    private static bool TryDeleteTemporaryBundle(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }
            if (OperatingSystem.IsWindows())
            {
                foreach (string file in Directory.EnumerateFiles(
                    path,
                    "*",
                    SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
            }
            else
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
                foreach (string directory in Directory.EnumerateDirectories(
                    path,
                    "*",
                    SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(
                        directory,
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
                }
                foreach (string file in Directory.EnumerateFiles(
                    path,
                    "*",
                    SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(
                        file,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private Uri ValidateAssetApiUri(string value, long id)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !IsCanonicalHttpsUri(uri) ||
            !string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.UnsafeReleaseAsset,
                "A GitHub release asset API URL is unsafe.");
        }
        string expected =
            $"/repos/{m_settings.Owner}/{m_settings.Repository}/releases/assets/{id}";
        if (!string.Equals(uri.AbsolutePath, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.UnsafeReleaseAsset,
                "A GitHub release asset API URL does not match the configured repository.");
        }
        return uri;
    }

    private static void ValidateAllowedDownloadUri(Uri uri)
    {
        if (!IsCanonicalHttpsUri(uri) ||
            !AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.UnsafeReleaseAsset,
                "A GitHub release asset redirected outside the reviewed HTTPS hosts.");
        }
    }

    private static bool IsCanonicalHttpsUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.IsDefaultPort || uri.Port == 443) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsSafeAssetName(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > ReleasePackagePath.MaximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value is "." or "..")
        {
            return false;
        }
        return value.All(character => !char.IsControl(character));
    }

    private static bool TryParseReleaseTag(
        string tag,
        out ReleaseSemanticVersion version)
    {
        const string prefix = "aethersdr-";
        version = default;
        if (!tag.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            if (!string.Equals(
                    InstallationReleaseIdentity.Parse(tag),
                    tag,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        string value = tag[prefix.Length..];
        if (!ReleaseSemanticVersion.TryParse(value, out version))
        {
            return false;
        }
        return string.Equals(value, FormatSemanticVersion(version), StringComparison.Ordinal);
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

    private static byte[]? OptionalSha256Digest(JsonElement element)
    {
        if (!element.TryGetProperty("digest", out JsonElement digestElement) ||
            digestElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (digestElement.ValueKind != JsonValueKind.String)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "A GitHub release asset digest is malformed.");
        }
        string value = digestElement.GetString() ?? string.Empty;
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + 64 ||
            value[prefix.Length..].Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "A GitHub release asset digest is not canonical SHA-256.");
        }
        return Convert.FromHexString(value[prefix.Length..]);
    }

    private static JsonElement RequiredProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub release metadata is missing a required field.");
        }
        return value;
    }

    private static string RequiredString(
        JsonElement element,
        string name,
        int maximumLength)
    {
        JsonElement value = RequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub release metadata contains a non-string field.");
        }
        string text = value.GetString() ?? string.Empty;
        if (text.Length is 0 || text.Length > maximumLength ||
            !string.Equals(text, text.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub release metadata contains an invalid string field.");
        }
        return text;
    }

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        JsonElement value = RequiredProperty(element, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub release metadata contains a non-boolean field.");
        }
        return value.GetBoolean();
    }

    private static long RequiredPositiveInt64(JsonElement element, string name)
    {
        JsonElement value = RequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long parsed) ||
            parsed <= 0)
        {
            throw Failure(
                GitHubReleaseBundleFailureCode.InvalidReleaseMetadata,
                "GitHub release metadata contains an invalid positive integer.");
        }
        return parsed;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static string ValidateTemporaryRoot(string? value)
    {
        string root = value?.Trim() ?? string.Empty;
        if (root.Length is 0 or >
                LocalOfflineReleaseBundleVerificationService.MaximumBundlePathLength ||
            !string.Equals(root, value, StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException(
                "GitHub release checking requires one canonical absolute temporary root.");
        }
        string fullPath = Path.GetFullPath(root);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(root, fullPath, comparison))
        {
            throw new InvalidOperationException(
                "The GitHub release temporary root must not contain relative segments.");
        }
        return fullPath;
    }

    private static ValidatedSettings ValidateSettings(
        GitHubReleaseSourceSettings? settings)
    {
        settings ??= new GitHubReleaseSourceSettings();
        string owner = ValidateRepositoryToken(
            settings.Owner,
            "owner",
            allowPeriodAndUnderscore: false);
        string repository = ValidateRepositoryToken(
            settings.Repository,
            "repository",
            allowPeriodAndUnderscore: true);
        if (settings.MaximumReleaseCount is < 1 or > 100)
        {
            throw new InvalidOperationException(
                $"{GitHubReleaseSourceSettings.SectionName}:MaximumReleaseCount " +
                "must be from 1 through 100.");
        }
        if (settings.RequestTimeoutSeconds is < 5 or > 120)
        {
            throw new InvalidOperationException(
                $"{GitHubReleaseSourceSettings.SectionName}:RequestTimeoutSeconds " +
                "must be from 5 through 120.");
        }
        return new ValidatedSettings(
            settings.Enabled,
            owner,
            repository,
            settings.MaximumReleaseCount,
            settings.RequestTimeoutSeconds);
    }

    private static string ValidateRepositoryToken(
        string? value,
        string name,
        bool allowPeriodAndUnderscore)
    {
        string token = value?.Trim() ?? string.Empty;
        if (token.Length is 0 or > 100 ||
            !string.Equals(token, value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{GitHubReleaseSourceSettings.SectionName}:{name} requires one " +
                "canonical value from 1 through 100 characters.");
        }
        foreach (char character in token)
        {
            bool valid = char.IsAsciiLetterOrDigit(character) || character == '-' ||
                (allowPeriodAndUnderscore && character is '.' or '_');
            if (!valid)
            {
                throw new InvalidOperationException(
                    $"{GitHubReleaseSourceSettings.SectionName}:{name} contains an " +
                    "unsupported character.");
            }
        }
        return token;
    }

    private static GitHubReleaseException Failure(
        GitHubReleaseBundleFailureCode failureCode,
        string message,
        int examinedReleaseCount = 0,
        int downloadedAssetCount = 0,
        long downloadedBytes = 0) =>
        new(
            failureCode,
            message,
            examinedReleaseCount,
            downloadedAssetCount,
            downloadedBytes);

    private sealed record ValidatedSettings(
        bool Enabled,
        string Owner,
        string Repository,
        int MaximumReleaseCount,
        int RequestTimeoutSeconds);

    private sealed record GitHubRelease(
        string TagName,
        bool Draft,
        bool Prerelease,
        IReadOnlyList<GitHubReleaseAsset> Assets);

    private sealed record GitHubReleaseAsset(
        long Id,
        string Name,
        long Size,
        Uri ApiUri,
        byte[]? Sha256);

    private sealed record RequiredAssets(
        GitHubReleaseAsset Manifest,
        IReadOnlyList<GitHubReleaseAsset> Packages);

    private sealed record DownloadedAsset(long Length);

    private sealed class GitHubReleaseException(
        GitHubReleaseBundleFailureCode failureCode,
        string message,
        int examinedReleaseCount,
        int downloadedAssetCount,
        long downloadedBytes) : Exception(message)
    {
        internal GitHubReleaseBundleFailureCode FailureCode { get; } = failureCode;
        internal int ExaminedReleaseCount { get; } = examinedReleaseCount;
        internal int DownloadedAssetCount { get; } = downloadedAssetCount;
        internal long DownloadedBytes { get; } = downloadedBytes;
    }
}

/// <summary>
/// Read-only CLI adapter for checking one selected GitHub release through the
/// existing immutable local signed-bundle verifier. It persists no downloaded
/// release and owns no extraction, staging, installation, activation, rollback,
/// service-control, Admin, browser, radio, watchdog, command, lease, or TX path.
/// </summary>
public sealed class GitHubReleaseBundleCheckConsole
{
    public const int SuccessExitCode = 0;
    public const int VerificationFailedExitCode = 2;

    private const int CurrentReportVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly GitHubReleaseBundleSource m_source;
    private readonly Func<ReleaseManifestArchitecture> m_architectureResolver;

    public GitHubReleaseBundleCheckConsole(GitHubReleaseBundleSource source)
        : this(source, ResolveCurrentArchitecture)
    {
    }

    internal GitHubReleaseBundleCheckConsole(
        GitHubReleaseBundleSource source,
        Func<ReleaseManifestArchitecture> architectureResolver)
    {
        m_source = source ?? throw new ArgumentNullException(nameof(source));
        m_architectureResolver = architectureResolver ??
            throw new ArgumentNullException(nameof(architectureResolver));
        Snapshot = new GitHubReleaseBundleCheckConsoleDiagnostics(
            Registered: true,
            GitHubSourceRegistered: true,
            NetworkReadRegistered: true,
            TemporaryBundleRegistered: true,
            LocalSignedVerificationRegistered: true,
            PersistentDownloadRegistered: false,
            ArchiveExtractionRegistered: false,
            StagingRegistered: false,
            InstallationRegistered: false,
            ActivationRegistered: false,
            RollbackRegistered: false,
            MigrationRegistered: false,
            ServiceControlRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public GitHubReleaseBundleCheckConsoleDiagnostics Snapshot { get; }

    public async Task<int> ExecuteAsync(
        ReleaseUpdateConsoleCommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (commandLine.Command != ReleaseUpdateConsoleCommandKind.CheckGitHubRelease ||
            commandLine.UpdateChannel is null ||
            commandLine.ConfigurationSchemaVersion is null ||
            commandLine.ProtocolVersion is null)
        {
            throw new InvalidOperationException(
                "The GitHub release check console requires one complete GitHub release command.");
        }

        ReleaseManifestVerificationContext context = new(
            m_architectureResolver(),
            commandLine.UpdateChannel.Value,
            commandLine.PinnedReleaseIdentity,
            commandLine.InstalledVersion,
            commandLine.ConfigurationSchemaVersion.Value,
            commandLine.ProtocolVersion.Value);
        GitHubReleaseBundleCheckResult result = await m_source.CheckAsync(
            context,
            cancellationToken).ConfigureAwait(false);
        int exitCode = result.Succeeded
            ? SuccessExitCode
            : VerificationFailedExitCode;
        GitHubReleaseBundleCheckReport report = new(
            CurrentReportVersion,
            "checkGitHubRelease",
            result.Succeeded,
            exitCode,
            result.FailureCode,
            result.Message,
            result.ExaminedReleaseCount,
            result.DownloadedAssetCount,
            result.DownloadedBytes,
            result.Verification);
        await output.WriteLineAsync(
            JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);
        return exitCode;
    }

    private static ReleaseManifestArchitecture ResolveCurrentArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 =>
                ReleaseManifestArchitecture.LinuxX64,
            System.Runtime.InteropServices.Architecture.Arm64 =>
                ReleaseManifestArchitecture.LinuxArm64,
            _ => ReleaseManifestArchitecture.Unknown
        };

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
