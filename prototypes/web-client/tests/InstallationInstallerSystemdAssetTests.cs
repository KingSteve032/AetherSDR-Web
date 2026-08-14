namespace AetherSDR.Web.Tests;

public sealed class InstallationInstallerSystemdAssetTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedStarts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aethersdr-web.service"] =
                "/opt/aethersdr/current/gateway-web/AetherSDR.Web",
            ["aethersdr-release-updater.service"] =
                "/opt/aethersdr/current/gateway-web/AetherSDR.Web --release-update-supervisor",
            ["aetherremote-broker.service"] =
                "/opt/aethersdr/current/broker/AetherRemote.Broker",
            ["aetherremote-station-engine.service"] =
                "/opt/aethersdr/current/station-engine/AetherSDR.Web",
            ["aetherremote-agent.service"] =
                "/opt/aethersdr/current/aetherremote-agent/AetherRemote.Agent"
        };

    [Fact]
    public void InstallerUnitsUseCanonicalImmutableReleaseLayout()
    {
        string directory = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "web-client",
            "deploy",
            "installer",
            "systemd");
        string[] files = Directory
            .EnumerateFiles(directory, "*.service")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ExpectedStarts.Keys.Order(StringComparer.Ordinal),
            files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (string file in files)
        {
            string content = File.ReadAllText(file);
            string name = Path.GetFileName(file);
            Assert.Contains(
                $"ExecStart={ExpectedStarts[name]}",
                content,
                StringComparison.Ordinal);
            Assert.Contains("ProtectSystem=strict", content);
            Assert.Contains("ProtectHome=true", content);
            Assert.Contains("NoNewPrivileges=true", content);
            Assert.Contains("PrivateDevices=true", content);
            Assert.Contains("RestrictNamespaces=true", content);
            Assert.Contains("RestrictSUIDSGID=true", content);
            Assert.Contains("CapabilityBoundingSet=", content);
            Assert.Contains("AmbientCapabilities=", content);
            Assert.Contains("UMask=0077", content);
            Assert.DoesNotContain("/home/flexweb", content);
            Assert.DoesNotContain("/opt/aetherremote", content);
            Assert.DoesNotContain("Environment=DOTNET_ENVIRONMENT=Development", content);
            Assert.DoesNotContain("Environment=ASPNETCORE_ENVIRONMENT=Development", content);
        }
    }

    [Fact]
    public void ReleaseUpdaterIsRootFixedPurposeWithOnlyRollbackCapabilities()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "web-client",
            "deploy",
            "installer",
            "systemd",
            "aethersdr-release-updater.service");
        string content = File.ReadAllText(path);

        Assert.Contains("User=root", content, StringComparison.Ordinal);
        Assert.Contains("Group=aethersdr", content, StringComparison.Ordinal);
        Assert.Contains(
            "CapabilityBoundingSet=CAP_CHOWN CAP_DAC_OVERRIDE CAP_FOWNER",
            content,
            StringComparison.Ordinal);
        Assert.Contains("AmbientCapabilities=\n", content, StringComparison.Ordinal);
        Assert.Contains("NoNewPrivileges=true", content, StringComparison.Ordinal);
        Assert.Contains("Restart=always", content, StringComparison.Ordinal);
        Assert.Contains("RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6", content, StringComparison.Ordinal);
        Assert.Contains("IPAddressDeny=any", content, StringComparison.Ordinal);
        Assert.Contains("IPAddressAllow=localhost", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/bin/sh", content, StringComparison.Ordinal);
        Assert.DoesNotContain("bash", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebAndStationUnitsUseWritableOwnerOnlyDataProtectionPaths()
    {
        string directory = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "web-client",
            "deploy",
            "installer",
            "systemd");
        string web = File.ReadAllText(
            Path.Combine(directory, "aethersdr-web.service"));
        string station = File.ReadAllText(
            Path.Combine(directory, "aetherremote-station-engine.service"));

        Assert.Contains(
            "Environment=DataProtection__KeyPath=/var/lib/aethersdr/secrets/data-protection",
            web,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadWritePaths=/var/lib/aethersdr /var/log/aethersdr",
            web,
            StringComparison.Ordinal);
        Assert.Contains(
            "Environment=DataProtection__KeyPath=/var/lib/aethersdr/aetherremote/station-engine/data-protection",
            station,
            StringComparison.Ordinal);
        Assert.Contains(
            "Environment=InstallationServiceHost__Role=StationEngine",
            station,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReadWritePaths=/var/lib/aethersdr/aetherremote/station-engine /var/log/aethersdr/aetherremote",
            station,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WebRolePackageCarriesInstallerUnitsAtOneFixedPath()
    {
        string project = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "prototypes",
                "web-client",
                "AetherSDR.Web.csproj"));

        Assert.Contains(
            @"deploy\installer\systemd\*.service",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            @"installer\systemd\%(Filename)%(Extension)",
            project,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            Count(
                project,
                @"deploy\installer\systemd\*.service"));
    }

    private static int Count(string value, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(
                   needle,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(current.FullName, "AetherSDR-Web.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "The repository root could not be located.");
    }
}
