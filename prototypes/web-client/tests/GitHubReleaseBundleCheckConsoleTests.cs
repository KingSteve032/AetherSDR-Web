using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class GitHubReleaseBundleCheckConsoleTests
{
    private const string TestKeyId = "m8b-github-test-key";
    private const string TestPrivateKeyPkcs8Base64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg" +
        "EjRWeJq83vEjRWeJq83vEjRWeJq83vEjRWeJq83vEjShRAN" +
        "CAARawLjuCeZXZ7tsfTRAu+FcuRLUr+ELbhoX/6Hs0fLlSZe" +
        "0NNZYPUqZa65oYGMMs9Ud19Qc/RZMzn4vZv5+EakU";
    private const string TestPublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEWsC47gnmV2e" +
        "7bH00QLvhXLkS1K/hC24aF/+h7NHy5UmXtDTWWD1KmWuuaG" +
        "BjDLPVHdfUHP0WTM5+L2b+fhGpFA==";

    [Fact]
    public void CompleteGitHubCommandParsesWithSharedCompatibilityInputs()
    {
        ReleaseUpdateConsoleCommandLine parsed =
            ReleaseUpdateConsoleCommandParser.Parse(
            [
                ReleaseUpdateConsoleCommandParser.CheckGitHubReleaseSwitch,
                ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch,
                "8.1.0",
                ReleaseUpdateConsoleCommandParser.UpdateChannelSwitch,
                "stable",
                ReleaseUpdateConsoleCommandParser.ConfigurationSchemaVersionSwitch,
                "1",
                ReleaseUpdateConsoleCommandParser.ProtocolVersionSwitch,
                "2",
                "--urls",
                "http://127.0.0.1:5080"
            ]);

        Assert.Equal(
            ReleaseUpdateConsoleCommandKind.CheckGitHubRelease,
            parsed.Command);
        Assert.Equal(string.Empty, parsed.BundleDirectory);
        Assert.Equal("8.1.0", parsed.InstalledVersion);
        Assert.Equal(InstallationUpdateChannel.Stable, parsed.UpdateChannel);
        Assert.Equal(1, parsed.ConfigurationSchemaVersion);
        Assert.Equal(2, parsed.ProtocolVersion);
        Assert.Equal(
            ["--urls", "http://127.0.0.1:5080"],
            parsed.ApplicationArguments);
    }

    [Fact]
    public void PublicSurfaceAndDiagnosticsRemainReadOnlyAndFailClosed()
    {
        using Fixture fixture = new(sourceEnabled: false, trustEnabled: false);
        string[] sourceMethods = typeof(GitHubReleaseBundleSource)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] consoleMethods = typeof(GitHubReleaseBundleCheckConsole)
            .GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["get_Snapshot"], sourceMethods);
        Assert.Equal(["ExecuteAsync", "get_Snapshot"], consoleMethods);

        GitHubReleaseBundleSourceDiagnostics source = fixture.Source.Snapshot;
        Assert.True(source.Registered);
        Assert.False(source.Enabled);
        Assert.True(source.RepositoryConfigured);
        Assert.True(source.GitHubMetadataReadRegistered);
        Assert.True(source.GitHubAssetDownloadRegistered);
        Assert.True(source.TemporaryBundleWriteRegistered);
        Assert.True(source.TemporaryBundleCleanupRegistered);
        Assert.True(source.LocalSignedVerificationRegistered);
        Assert.Equal(32, source.MaximumReleaseCount);
        Assert.Equal(30, source.RequestTimeoutSeconds);
        Assert.False(source.PersistentDownloadRegistered);
        Assert.False(source.ArchiveExtractionRegistered);
        Assert.False(source.StagingRegistered);
        Assert.False(source.InstallationRegistered);
        Assert.False(source.ActivationRegistered);
        Assert.False(source.RollbackRegistered);
        Assert.False(source.MigrationRegistered);
        Assert.False(source.ServiceControlRegistered);
        Assert.False(source.AdminCallerRegistered);
        Assert.False(source.BrowserCallerRegistered);
        Assert.False(source.RadioCallerRegistered);
        Assert.False(source.WatchdogCallerRegistered);
        Assert.False(source.CommandCallerRegistered);
        Assert.False(source.LeaseCallerRegistered);
        Assert.False(source.TxCallerRegistered);
        Assert.Equal("disabled", source.Reason);

        GitHubReleaseBundleCheckConsoleDiagnostics console =
            fixture.Console.Snapshot;
        Assert.True(console.Registered);
        Assert.True(console.GitHubSourceRegistered);
        Assert.True(console.NetworkReadRegistered);
        Assert.True(console.TemporaryBundleRegistered);
        Assert.True(console.LocalSignedVerificationRegistered);
        Assert.False(console.PersistentDownloadRegistered);
        Assert.False(console.ArchiveExtractionRegistered);
        Assert.False(console.StagingRegistered);
        Assert.False(console.InstallationRegistered);
        Assert.False(console.ActivationRegistered);
        Assert.False(console.RollbackRegistered);
        Assert.False(console.MigrationRegistered);
        Assert.False(console.ServiceControlRegistered);
        Assert.False(console.AdminCallerRegistered);
        Assert.False(console.BrowserCallerRegistered);
        Assert.False(console.RadioCallerRegistered);
        Assert.False(console.WatchdogCallerRegistered);
        Assert.False(console.CommandCallerRegistered);
        Assert.False(console.LeaseCallerRegistered);
        Assert.False(console.TxCallerRegistered);
    }

    [Theory]
    [InlineData("bad owner", "AetherSDR-Web", 32, 30)]
    [InlineData("KingSteve032", "bad/repository", 32, 30)]
    [InlineData("KingSteve032", "AetherSDR-Web", 0, 30)]
    [InlineData("KingSteve032", "AetherSDR-Web", 101, 30)]
    [InlineData("KingSteve032", "AetherSDR-Web", 32, 4)]
    [InlineData("KingSteve032", "AetherSDR-Web", 32, 121)]
    public void InvalidSettingsFailAtConstruction(
        string owner,
        string repository,
        int maximumReleaseCount,
        int timeoutSeconds)
    {
        using Fixture fixture = new(sourceEnabled: false, trustEnabled: false);
        GitHubReleaseSourceSettings settings = new()
        {
            Enabled = true,
            Owner = owner,
            Repository = repository,
            MaximumReleaseCount = maximumReleaseCount,
            RequestTimeoutSeconds = timeoutSeconds
        };

        Assert.Throws<InvalidOperationException>(() =>
            new GitHubReleaseBundleSource(
                settings,
                fixture.CreateClient,
                fixture.BundleService,
                fixture.TemporaryRoot,
                NullLogger<GitHubReleaseBundleSource>.Instance));
    }

    [Fact]
    public async Task DisabledSourceRejectsBeforeAnyNetworkRequest()
    {
        using Fixture fixture = new(sourceEnabled: false, trustEnabled: true);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal(
            "sourceDisabled",
            document.RootElement.GetProperty("failureCode").GetString());
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task DisabledTrustRejectsBeforeAnyNetworkRequest()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: false);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal(
            "verificationTrustUnavailable",
            document.RootElement.GetProperty("failureCode").GetString());
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task ValidStableReleaseDownloadsFiveAssetsVerifiesAndCleansUp()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease release = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 100);
        fixture.RegisterReleases(release);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        string json = output.ToString();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(0, exitCode);
        Assert.True(root.GetProperty("succeeded").GetBoolean());
        Assert.Equal("none", root.GetProperty("failureCode").GetString());
        Assert.Equal(1, root.GetProperty("examinedReleaseCount").GetInt32());
        Assert.Equal(5, root.GetProperty("downloadedAssetCount").GetInt32());
        Assert.Equal(
            "aethersdr-8.2.0",
            root.GetProperty("verification")
                .GetProperty("releaseIdentity")
                .GetString());
        Assert.Equal(6, fixture.Handler.Requests.Count);
        Assert.Contains(
            fixture.Handler.Requests,
            request =>
                request.Accept.Contains("application/vnd.github+json") &&
                request.ApiVersion == GitHubReleaseHttpClient.ApiVersion &&
                request.UserAgent.Contains("AetherSDR-Web/1.0"));
        Assert.Equal(
            5,
            fixture.Handler.Requests.Count(request =>
                request.Accept.Contains("application/octet-stream")));
        Assert.DoesNotContain(fixture.TemporaryRoot, json, StringComparison.Ordinal);
        Assert.DoesNotContain("api.github.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKeyId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            release.Assets[0].Digest,
            json,
            StringComparison.Ordinal);
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task StableSelectionSkipsDraftAndPrereleaseAndChoosesHighestStable()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease draft = TestRelease.Create(
            "aethersdr-9.0.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 200,
            draft: true);
        TestRelease beta = TestRelease.Create(
            "aethersdr-8.3.0-beta.1",
            ReleaseManifestChannel.Beta,
            prerelease: true,
            firstAssetId: 300);
        TestRelease lower = TestRelease.Create(
            "aethersdr-8.1.5",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 400);
        TestRelease selected = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 500);
        fixture.RegisterReleases(draft, beta, lower, selected);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal(
            selected.TagName,
            document.RootElement.GetProperty("verification")
                .GetProperty("releaseIdentity")
                .GetString());
        Assert.DoesNotContain(
            fixture.Handler.Requests,
            request => request.Uri.AbsoluteUri.Contains("/assets/400", StringComparison.Ordinal));
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task BetaSelectionRequiresGitHubPrereleaseAndSignedBetaManifest()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease stable = TestRelease.Create(
            "aethersdr-8.3.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 600);
        TestRelease beta = TestRelease.Create(
            "aethersdr-8.4.0-beta.2",
            ReleaseManifestChannel.Beta,
            prerelease: true,
            firstAssetId: 700);
        fixture.RegisterReleases(stable, beta);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Beta),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal(
            beta.TagName,
            document.RootElement.GetProperty("verification")
                .GetProperty("releaseIdentity")
                .GetString());
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task PinnedSelectionRequiresExactReleaseIdentity()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease other = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Pinned,
            prerelease: false,
            firstAssetId: 800);
        TestRelease selected = TestRelease.Create(
            "aethersdr-8.2.1",
            ReleaseManifestChannel.Pinned,
            prerelease: false,
            firstAssetId: 900);
        fixture.RegisterReleases(other, selected);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(
                InstallationUpdateChannel.Pinned,
                pinnedIdentity: selected.TagName),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal(
            selected.TagName,
            document.RootElement.GetProperty("verification")
                .GetProperty("releaseIdentity")
                .GetString());
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task MissingRequiredAssetFailsBeforeAnyAssetDownload()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease release = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 1000);
        release.Assets.RemoveAll(asset =>
            asset.Name.StartsWith("aetherremote-agent-", StringComparison.Ordinal));
        fixture.RegisterReleases(release);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.Equal(
            "missingReleaseAsset",
            document.RootElement.GetProperty("failureCode").GetString());
        Assert.Single(fixture.Handler.Requests);
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task DuplicateRequiredAssetFailsBeforeAnyAssetDownload()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease release = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 1100);
        TestAsset duplicate = release.Assets[0] with
        {
            Id = 1199,
            ApiUrl = AssetApiUrl(1199)
        };
        release.Assets.Add(duplicate);
        fixture.RegisterReleases(release);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.Equal(
            "duplicateReleaseAsset",
            document.RootElement.GetProperty("failureCode").GetString());
        Assert.Single(fixture.Handler.Requests);
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task AssetApiUrlMustRemainBoundToConfiguredRepository()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease release = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 1200);
        release.Assets[0] = release.Assets[0] with
        {
            ApiUrl = "https://api.github.com/repos/other/repository/releases/assets/1200"
        };
        fixture.RegisterReleases(release);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.Equal(
            "unsafeReleaseAsset",
            document.RootElement.GetProperty("failureCode").GetString());
        Assert.Single(fixture.Handler.Requests);
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task AssetLengthDriftFailsAndTemporaryBundleIsRemoved()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease release = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 1300);
        release.Assets[0] = release.Assets[0] with
        {
            Size = release.Assets[0].Size + 1
        };
        fixture.RegisterReleases(release);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.Equal(
            "releaseAssetChanged",
            document.RootElement.GetProperty("failureCode").GetString());
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task AssetDigestDriftFailsAndTemporaryBundleIsRemoved()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease release = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 1400);
        release.Assets[0] = release.Assets[0] with
        {
            Digest = "sha256:" + new string('0', 64)
        };
        fixture.RegisterReleases(release);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.Equal(
            "releaseAssetChanged",
            document.RootElement.GetProperty("failureCode").GetString());
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task SignedReleaseIdentityMustMatchSelectedGitHubTag()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease release = TestRelease.Create(
            "aethersdr-8.2.1",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 1500,
            signedIdentity: "aethersdr-8.2.0");
        fixture.RegisterReleases(release);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.Equal(
            "releaseIdentityMismatch",
            document.RootElement.GetProperty("failureCode").GetString());
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task AssetRedirectOutsideReviewedGitHubHostsFailsClosed()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease release = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 1600);
        fixture.RegisterReleases(release);
        fixture.Handler.SetResponse(
            new Uri(release.Assets[0].ApiUrl),
            _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers =
                {
                    Location = new Uri("https://example.com/release-manifest.json")
                }
            });
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);
        using JsonDocument document = JsonDocument.Parse(output.ToString());

        Assert.Equal(2, exitCode);
        Assert.Equal(
            "unsafeReleaseAsset",
            document.RootElement.GetProperty("failureCode").GetString());
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task AllowedGitHubAssetRedirectIsFollowedWithinBound()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        TestRelease release = TestRelease.Create(
            "aethersdr-8.2.0",
            ReleaseManifestChannel.Stable,
            prerelease: false,
            firstAssetId: 1700);
        fixture.RegisterReleases(release);
        TestAsset manifest = release.Assets[0];
        Uri redirected = new(
            "https://release-assets.githubusercontent.com/aethersdr/test-manifest");
        fixture.Handler.SetResponse(
            new Uri(manifest.ApiUrl),
            _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = redirected }
            });
        fixture.Handler.SetBytes(redirected, manifest.Content);
        using StringWriter output = new();

        int exitCode = await fixture.Console.ExecuteAsync(
            Command(InstallationUpdateChannel.Stable),
            output);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            fixture.Handler.Requests,
            request => request.Uri == redirected);
        AssertTemporaryRootEmpty(fixture);
    }

    [Fact]
    public async Task CancellationAndWrongCommandFailBeforeNetwork()
    {
        using Fixture fixture = new(sourceEnabled: true, trustEnabled: true);
        using StringWriter output = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Console.ExecuteAsync(
                Command(InstallationUpdateChannel.Stable),
                output,
                cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Console.ExecuteAsync(
                ReleaseUpdateConsoleCommandLine.None([]),
                output));
        Assert.Empty(fixture.Handler.Requests);
        Assert.Equal(string.Empty, output.ToString());
        AssertTemporaryRootEmpty(fixture);
    }

    private static ReleaseUpdateConsoleCommandLine Command(
        InstallationUpdateChannel channel,
        string pinnedIdentity = "") =>
        new(
            ReleaseUpdateConsoleCommandKind.CheckGitHubRelease,
            string.Empty,
            "8.1.0",
            channel,
            pinnedIdentity,
            1,
            2,
            []);

    private static void AssertTemporaryRootEmpty(Fixture fixture) =>
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.TemporaryRoot));

    private static string AssetApiUrl(long id) =>
        $"https://api.github.com/repos/KingSteve032/AetherSDR-Web/releases/assets/{id}";

    private sealed class Fixture : IDisposable
    {
        private readonly string m_keyDirectory;

        internal Fixture(
            bool sourceEnabled,
            bool trustEnabled,
            ReleaseManifestArchitecture architecture =
                ReleaseManifestArchitecture.LinuxX64)
        {
            TemporaryRoot = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-github-temp-{Guid.NewGuid():N}");
            m_keyDirectory = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-github-trust-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TemporaryRoot);
            Directory.CreateDirectory(m_keyDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    TemporaryRoot,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    m_keyDirectory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }

            string keyPath = Path.Combine(m_keyDirectory, "release-public.pem");
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(TestPublicKeySpkiBase64),
                out _);
            File.WriteAllText(
                keyPath,
                key.ExportSubjectPublicKeyInfoPem(),
                new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    keyPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            ReleaseManifestTrustSettings trust = new()
            {
                VerificationEnabled = trustEnabled,
                Keys = trustEnabled
                    ?
                    [
                        new ReleaseManifestTrustKeySettings
                        {
                            KeyId = TestKeyId,
                            Algorithm =
                                ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                            PublicKeyPath = keyPath
                        }
                    ]
                    : []
            };
            ReleaseManifestTrustRegistry registry = new(
                Options.Create(trust),
                NullLogger<ReleaseManifestTrustRegistry>.Instance);
            SignedReleaseManifestVerificationService manifestService = new(
                registry,
                new SignedReleaseManifestVerifier());
            BundleService = new LocalOfflineReleaseBundleVerificationService(
                manifestService);
            Handler = new RoutingHandler();
            GitHubReleaseSourceSettings source = new()
            {
                Enabled = sourceEnabled,
                Owner = "KingSteve032",
                Repository = "AetherSDR-Web",
                MaximumReleaseCount = 32,
                RequestTimeoutSeconds = 30
            };
            Source = new GitHubReleaseBundleSource(
                source,
                CreateClient,
                BundleService,
                TemporaryRoot,
                NullLogger<GitHubReleaseBundleSource>.Instance);
            Console = new GitHubReleaseBundleCheckConsole(
                Source,
                () => architecture);
        }

        internal string TemporaryRoot { get; }
        internal RoutingHandler Handler { get; }
        internal LocalOfflineReleaseBundleVerificationService BundleService { get; }
        internal GitHubReleaseBundleSource Source { get; }
        internal GitHubReleaseBundleCheckConsole Console { get; }

        internal HttpClient CreateClient() =>
            new(Handler, disposeHandler: false);

        internal void RegisterReleases(params TestRelease[] releases)
        {
            Uri metadataUri = new(
                "https://api.github.com/repos/KingSteve032/AetherSDR-Web/releases?per_page=32");
            Handler.SetJson(
                metadataUri,
                JsonSerializer.SerializeToUtf8Bytes(
                    releases.Select(release => release.ToMetadata()).ToArray()));
            foreach (TestRelease release in releases)
            {
                foreach (TestAsset asset in release.Assets)
                {
                    Handler.SetBytes(new Uri(asset.ApiUrl), asset.Content);
                }
            }
        }

        public void Dispose()
        {
            Handler.Dispose();
            DeleteDirectory(TemporaryRoot);
            DeleteDirectory(m_keyDirectory);
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }
                if (!OperatingSystem.IsWindows())
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
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, Func<HttpRequestMessage, HttpResponseMessage>>
            m_responses = [];

        internal List<CapturedRequest> Requests { get; } = [];

        internal void SetJson(Uri uri, byte[] content) =>
            SetBytes(uri, content, "application/json");

        internal void SetBytes(
            Uri uri,
            byte[] content,
            string contentType = "application/octet-stream") =>
            SetResponse(
                uri,
                _ =>
                {
                    ByteArrayContent body = new(content);
                    body.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = body,
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri)
                    };
                });

        internal void SetResponse(
            Uri uri,
            Func<HttpRequestMessage, HttpResponseMessage> response) =>
            m_responses[uri] = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = request.RequestUri ?? throw new InvalidOperationException();
            Requests.Add(new CapturedRequest(
                uri,
                string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
                request.Headers.TryGetValues(
                    "X-GitHub-Api-Version",
                    out IEnumerable<string>? versions)
                    ? string.Join(",", versions)
                    : string.Empty,
                request.Headers.UserAgent.ToString()));
            if (!m_responses.TryGetValue(uri, out var factory))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = new HttpRequestMessage(request.Method, uri)
                });
            }
            HttpResponseMessage response = factory(request);
            response.RequestMessage ??= new HttpRequestMessage(request.Method, uri);
            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string Accept,
        string ApiVersion,
        string UserAgent);

    private sealed class TestRelease
    {
        private TestRelease(
            string tagName,
            bool draft,
            bool prerelease,
            List<TestAsset> assets)
        {
            TagName = tagName;
            Draft = draft;
            Prerelease = prerelease;
            Assets = assets;
        }

        internal string TagName { get; }
        internal bool Draft { get; }
        internal bool Prerelease { get; }
        internal List<TestAsset> Assets { get; }

        internal static TestRelease Create(
            string tagName,
            ReleaseManifestChannel channel,
            bool prerelease,
            long firstAssetId,
            bool draft = false,
            string? signedIdentity = null)
        {
            string releaseIdentity = signedIdentity ?? tagName;
            const string prefix = "aethersdr-";
            string version = releaseIdentity.StartsWith(prefix, StringComparison.Ordinal)
                ? releaseIdentity[prefix.Length..]
                : throw new InvalidOperationException();
            string[] packageNames =
            [
                "aethersdr-gateway-linux-x64.tar.gz",
                "aethersdr-broker-linux-x64.tar.gz",
                "aetherremote-agent-linux-x64.tar.gz",
                "aethersdr-station-engine-linux-x64.tar.gz"
            ];
            byte[][] packages =
            [
                Encoding.UTF8.GetBytes($"gateway-{tagName}"),
                Encoding.UTF8.GetBytes($"broker-{tagName}"),
                Encoding.UTF8.GetBytes($"agent-{tagName}"),
                Encoding.UTF8.GetBytes($"station-{tagName}")
            ];
            SignedReleaseManifestPayload payload = new()
            {
                SchemaVersion = SignedReleaseManifestPayload.CurrentSchemaVersion,
                ReleaseIdentity = releaseIdentity,
                Version = version,
                Channel = channel,
                Architecture = ReleaseManifestArchitecture.LinuxX64,
                Packages =
                [
                    Declaration(
                        "gateway-web",
                        ReleasePackageRole.GatewayWeb,
                        packageNames[0],
                        packages[0]),
                    Declaration(
                        "broker",
                        ReleasePackageRole.Broker,
                        packageNames[1],
                        packages[1]),
                    Declaration(
                        "aetherremote-agent",
                        ReleasePackageRole.AetherRemoteAgent,
                        packageNames[2],
                        packages[2]),
                    Declaration(
                        "station-engine",
                        ReleasePackageRole.StationEngine,
                        packageNames[3],
                        packages[3])
                ],
                Configuration = new ReleaseConfigurationCompatibility
                {
                    TargetSchemaVersion = 1,
                    MinimumCompatibleSchemaVersion = 1,
                    MaximumCompatibleSchemaVersion = 2
                },
                Protocol = new ReleaseProtocolCompatibility
                {
                    MinimumVersion = 1,
                    MaximumVersion = 3
                },
                MinimumPreviousVersion = "8.1.0",
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
                    Title = $"AetherSDR {version}",
                    Summary = "GitHub release bundle check test vector."
                }
            };
            byte[] manifest = Sign(payload);
            List<TestAsset> assets =
            [
                Asset(
                    firstAssetId,
                    "release-manifest-linux-x64.json",
                    manifest),
                Asset(firstAssetId + 1, packageNames[0], packages[0]),
                Asset(firstAssetId + 2, packageNames[1], packages[1]),
                Asset(firstAssetId + 3, packageNames[2], packages[2]),
                Asset(firstAssetId + 4, packageNames[3], packages[3])
            ];
            return new TestRelease(tagName, draft, prerelease, assets);
        }

        internal Dictionary<string, object?> ToMetadata() =>
            new()
            {
                ["tag_name"] = TagName,
                ["draft"] = Draft,
                ["prerelease"] = Prerelease,
                ["assets"] = Assets.Select(asset => asset.ToMetadata()).ToArray()
            };

        private static TestAsset Asset(long id, string name, byte[] content)
        {
            string digest = Convert.ToHexString(SHA256.HashData(content))
                .ToLowerInvariant();
            return new TestAsset(
                id,
                name,
                content.LongLength,
                $"sha256:{digest}",
                AssetApiUrl(id),
                content);
        }

        private static SignedReleasePackage Declaration(
            string identity,
            ReleasePackageRole role,
            string assetName,
            byte[] content) =>
            new()
            {
                PackageIdentity = identity,
                Role = role,
                FileName = $"packages/{assetName}",
                Length = content.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(content))
                    .ToLowerInvariant()
            };

        private static byte[] Sign(SignedReleaseManifestPayload payload)
        {
            byte[] privateKeyBytes = Convert.FromBase64String(
                TestPrivateKeyPkcs8Base64);
            using ECDsa key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(privateKeyBytes, out int bytesRead);
                Assert.Equal(privateKeyBytes.Length, bytesRead);
                byte[] signingBytes = SignedReleaseManifestJson.CreateSigningBytes(
                    payload,
                    ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                    TestKeyId);
                byte[] signature = key.SignData(
                    signingBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                SignedReleaseManifestDocument document = new()
                {
                    Payload = payload,
                    Signature = new ReleaseManifestSignature
                    {
                        Algorithm =
                            ReleaseManifestSignatureAlgorithm.EcdsaP256Sha256,
                        KeyId = TestKeyId,
                        Value = ToBase64Url(signature)
                    }
                };
                return SignedReleaseManifestJson.Serialize(document);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKeyBytes);
            }
        }

        private static string ToBase64Url(ReadOnlySpan<byte> value) =>
            Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
    }

    private sealed record TestAsset(
        long Id,
        string Name,
        long Size,
        string Digest,
        string ApiUrl,
        byte[] Content)
    {
        internal Dictionary<string, object?> ToMetadata() =>
            new()
            {
                ["id"] = Id,
                ["name"] = Name,
                ["state"] = "uploaded",
                ["size"] = Size,
                ["digest"] = Digest,
                ["url"] = ApiUrl
            };
    }
}
