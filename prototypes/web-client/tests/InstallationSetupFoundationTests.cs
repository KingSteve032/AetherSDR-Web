using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupFoundationTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(
        "https://Radio.Example.org/",
        "https://radio.example.org")]
    [InlineData(
        "https://Radio.Example.org:443",
        "https://radio.example.org")]
    [InlineData(
        "https://Radio.Example.org:8443/",
        "https://radio.example.org:8443")]
    public void CanonicalPublicUrlNormalizesHttpsAuthority(
        string value,
        string expected)
    {
        CanonicalPublicUrl actual = CanonicalPublicUrl.Parse(value);

        Assert.Equal(expected, actual.Value);
        Assert.Equal(expected, actual.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("radio.example.org")]
    [InlineData("http://radio.example.org")]
    [InlineData("https://user@radio.example.org")]
    [InlineData("https://radio.example.org/setup")]
    [InlineData("https://radio.example.org?mode=setup")]
    [InlineData("https://radio.example.org/#setup")]
    public void CanonicalPublicUrlRejectsNonCanonicalInputs(string value)
    {
        Assert.Throws<InvalidOperationException>(
            () => CanonicalPublicUrl.Parse(value));
    }

    [Fact]
    public void TopologyProfilesKeepGatewayAndStationRolesExplicit()
    {
        InstallationTopologyProfile personal =
            InstallationTopologyProfile.For(
                InstallationTopologyKind.PersonalSingleStation);
        InstallationTopologyProfile remoteGateway =
            InstallationTopologyProfile.For(
                InstallationTopologyKind.RemoteStationGateway);
        InstallationTopologyProfile remoteNode =
            InstallationTopologyProfile.For(
                InstallationTopologyKind.RemoteStationNode);

        Assert.True(personal.GatewayRunsHere);
        Assert.True(personal.StationEngineRunsHere);
        Assert.True(personal.IntendedForOnePersonalStation);
        Assert.False(personal.AcceptsRemoteStations);
        Assert.True(remoteGateway.GatewayRunsHere);
        Assert.False(remoteGateway.StationEngineRunsHere);
        Assert.True(remoteGateway.AcceptsRemoteStations);
        Assert.False(remoteNode.GatewayRunsHere);
        Assert.True(remoteNode.AgentRunsHere);
        Assert.True(remoteNode.StationEngineRunsHere);
    }

    [Fact]
    public void DevelopmentPathsRemainUnderOnePortableRoot()
    {
        string contentRoot = Path.Combine(
            Path.GetTempPath(),
            "aethersdr-content-root");

        InstallationPaths paths = InstallationPaths.Resolve(
            contentRoot,
            InstallationPathLayout.Development);

        string expectedRoot = Path.Combine(
            Path.GetFullPath(contentRoot),
            ".aethersdr");
        Assert.StartsWith(expectedRoot, paths.ConfigurationDirectory);
        Assert.StartsWith(expectedRoot, paths.StateDirectory);
        Assert.StartsWith(expectedRoot, paths.SecretDirectory);
        Assert.Equal(
            Path.Combine(paths.StateDirectory, "setup", "installation.json"),
            paths.SetupStatePath);
        Assert.Equal(
            Path.Combine(paths.SecretDirectory, "data-protection"),
            paths.DataProtectionKeyDirectory);
        Assert.Equal(
            Path.Combine(paths.StateDirectory, "release-downloads"),
            paths.ReleaseDownloadDirectory);
    }

    [Fact]
    public void LinuxSystemPathsUseStandaloneLocations()
    {
        InstallationPaths paths = InstallationPaths.Resolve(
            "/unused-content-root",
            InstallationPathLayout.LinuxSystem);

        Assert.Equal("/etc/aethersdr", paths.ConfigurationDirectory);
        Assert.Equal("/var/lib/aethersdr", paths.StateDirectory);
        Assert.Equal(
            "/var/lib/aethersdr/secrets",
            paths.SecretDirectory);
        Assert.Equal("/opt/aethersdr/releases", paths.ReleaseDirectory);
        Assert.Equal("/var/backups/aethersdr", paths.BackupDirectory);
        Assert.Equal("/var/log/aethersdr", paths.LogDirectory);
        Assert.Equal(
            "/var/lib/aethersdr/release-downloads",
            paths.ReleaseDownloadDirectory);
    }

    [Fact]
    public void InstallationPathsRejectRelativeOrDuplicateOverrides()
    {
        Assert.Throws<InvalidOperationException>(
            () => InstallationPaths.Resolve(
                "/tmp/aethersdr",
                InstallationPathLayout.Development,
                new InstallationPathSettings
                {
                    StateDirectory = "relative-state"
                }));

        Assert.Throws<InvalidOperationException>(
            () => InstallationPaths.Resolve(
                "/tmp/aethersdr",
                InstallationPathLayout.Development,
                new InstallationPathSettings
                {
                    StateDirectory = "/srv/aethersdr/shared",
                    BackupDirectory = "/srv/aethersdr/shared"
                }));
    }

    [Fact]
    public async Task SetupStoreCreatesRestrictedVersionedStateAndUpdatesAtomically()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        string statePath = Path.Combine(
            temporary.Path,
            "setup",
            "installation.json");
        InstallationSetupStore store = new(statePath, time);

        InstallationSetupState initial = await store.LoadOrCreateAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        InstallationSetupState updated = await store.UpdateAsync(
            initial.Revision,
            state => state with
            {
                Topology = InstallationTopologyKind.PersonalSingleStation,
                LastCompletedStep = InstallationSetupStep.Topology
            });
        InstallationSetupState reloaded =
            await new InstallationSetupStore(statePath, time)
                .LoadOrCreateAsync();

        Assert.Equal(InstallationSetupState.CurrentSchemaVersion, initial.SchemaVersion);
        Assert.Equal(0, initial.Revision);
        Assert.Equal(1, updated.Revision);
        Assert.Equal(Start, initial.CreatedAt);
        Assert.Equal(Start.AddMinutes(1), updated.UpdatedAt);
        Assert.Equal(updated, reloaded);
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(statePath)!,
            "*.tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(statePath));
            Assert.Equal(
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(statePath)!));
        }
    }

    [Fact]
    public async Task SetupStoreRejectsOverlyBroadUnixPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        string statePath = Path.Combine(
            temporary.Path,
            "setup",
            "installation.json");
        InstallationSetupStore store = new(statePath, time);
        _ = await store.LoadOrCreateAsync();
        File.SetUnixFileMode(
            statePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new InstallationSetupStore(statePath, time)
                .LoadOrCreateAsync());
    }

    [Fact]
    public async Task SetupStoreRejectsStaleRevision()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = new(
            Path.Combine(temporary.Path, "setup", "installation.json"),
            time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationSetupState updated = await store.UpdateAsync(
            initial.Revision,
            state => state);

        InstallationSetupConcurrencyException exception =
            await Assert.ThrowsAsync<InstallationSetupConcurrencyException>(
                () => store.UpdateAsync(initial.Revision, state => state));

        Assert.Equal(initial.Revision, exception.ExpectedRevision);
        Assert.Equal(updated.Revision, exception.ActualRevision);
    }

    [Fact]
    public async Task BootstrapTokenIsPersistedOnlyAsHashAndClaimsOnce()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        string statePath = Path.Combine(
            temporary.Path,
            "setup",
            "installation.json");
        InstallationSetupStore store = new(statePath, time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService service = new(store, time);

        InstallationBootstrapTokenIssue issue = await service.IssueAsync(
            initial.Revision);
        string persisted = await File.ReadAllTextAsync(statePath);
        Assert.DoesNotContain(issue.Token, persisted);
        Assert.Equal(
            InstallationSetupLockMode.BootstrapRequired,
            issue.State.Lock.Mode);
        Assert.Equal(
            Start.Add(InstallationBootstrapTokenService.DefaultLifetime),
            issue.ExpiresAt);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ClaimAsync(issue.State.Revision, "incorrect-token-value-that-is-long-enough"));
        InstallationSetupState claimed = await service.ClaimAsync(
            issue.State.Revision,
            issue.Token);

        Assert.Equal(InstallationSetupLockMode.Claimed, claimed.Lock.Mode);
        Assert.Equal(InstallationSetupStep.BootstrapClaim, claimed.LastCompletedStep);
        Assert.Equal(string.Empty, claimed.Lock.BootstrapTokenHash);
        Assert.NotNull(claimed.Lock.ClaimedAt);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ClaimAsync(claimed.Revision, issue.Token));
    }

    [Fact]
    public async Task ReissuedBootstrapTokenPreservesResumableProgress()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = new(
            Path.Combine(temporary.Path, "setup", "installation.json"),
            time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService service = new(store, time);
        InstallationBootstrapTokenIssue first = await service.IssueAsync(
            initial.Revision);
        InstallationSetupState claimed = await service.ClaimAsync(
            first.State.Revision,
            first.Token);
        InstallationSetupState configured = await store.UpdateAsync(
            claimed.Revision,
            state => state with
            {
                Topology = InstallationTopologyKind.HybridGateway,
                LastCompletedStep = InstallationSetupStep.Topology
            });

        InstallationBootstrapTokenIssue reissued = await service.IssueAsync(
            configured.Revision);

        Assert.NotEqual(first.Token, reissued.Token);
        Assert.Equal(
            InstallationSetupStep.Topology,
            reissued.State.LastCompletedStep);
        Assert.Equal(
            InstallationTopologyKind.HybridGateway,
            reissued.State.Topology);
        Assert.Equal(
            InstallationSetupLockMode.BootstrapRequired,
            reissued.State.Lock.Mode);
    }

    [Fact]
    public async Task BootstrapTokenExpiresFailClosedWithoutChangingState()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        InstallationSetupStore store = new(
            Path.Combine(temporary.Path, "setup", "installation.json"),
            time);
        InstallationSetupState initial = await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService service = new(store, time);
        InstallationBootstrapTokenIssue issue = await service.IssueAsync(
            initial.Revision,
            TimeSpan.FromMinutes(2));
        time.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ClaimAsync(issue.State.Revision, issue.Token));
        InstallationSetupState reloaded = await store.LoadOrCreateAsync();

        Assert.Equal(issue.State.Revision, reloaded.Revision);
        Assert.Equal(
            InstallationSetupLockMode.BootstrapRequired,
            reloaded.Lock.Mode);
    }

    [Fact]
    public async Task SetupStoreRejectsUnknownPersistedFields()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider time = new(Start);
        string statePath = Path.Combine(
            temporary.Path,
            "setup",
            "installation.json");
        InstallationSetupStore store = new(statePath, time);
        _ = await store.LoadOrCreateAsync();
        string persisted = await File.ReadAllTextAsync(statePath);
        string malformed = persisted.Replace(
            "{",
            "{\n  \"unknownField\": true,",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(statePath, malformed);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                statePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new InstallationSetupStore(statePath, time)
                .LoadOrCreateAsync());
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration) => m_now += duration;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-setup-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    Path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
