using System.Reflection;
using AetherRemote.Agent;
using AetherRemote.Protocol;
using Microsoft.Extensions.Configuration;

namespace AetherRemote.Tests;

public sealed class AgentSettingsTests
{
    [Fact]
    public void SoftwareVersionDefaultsToCompiledAgentVersion()
    {
        Version? assemblyVersion =
            typeof(AgentSettings).Assembly.GetName().Version;

        Assert.NotNull(assemblyVersion);
        Assert.Equal(
            assemblyVersion!.ToString(3),
            new AgentSettings().SoftwareVersion);
        Assert.Equal("0.3.6", new AgentSettings().SoftwareVersion);
    }

    [Theory]
    [InlineData("/station/v1")]
    [InlineData("/station/v1/")]
    [InlineData("/aetherremote/broker/station/v1")]
    [InlineData("/aetherremote/broker/station/v1/")]
    public void BrokerEndpointPathsAcceptOnlyDirectOrCanonicalGatewayRoutes(
        string path)
    {
        Assert.True(AgentBrokerEndpointValidator.IsSupportedPath(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/station/v1//")]
    [InlineData("/aetherremote/station/v1")]
    [InlineData("/proxy/aetherremote/broker/station/v1")]
    [InlineData("/aetherremote/broker/station/v1/token")]
    public void BrokerEndpointPathsRejectAlternateOrMalformedRoutes(string path)
    {
        Assert.False(AgentBrokerEndpointValidator.IsSupportedPath(path));
    }

    [Fact]
    public void CapabilityGrantsMustBeExplicitAndKnown()
    {
        Assert.Throws<InvalidOperationException>(
            () => AgentCapabilityGrantValidator.Validate(null));

        AgentCapabilityGrantValidator.Validate([]);
        AgentCapabilityGrantValidator.Validate(
            [StationCapabilities.ReceiveProjectionV1]);

        Assert.Throws<InvalidOperationException>(
            () => AgentCapabilityGrantValidator.Validate(
                [
                    StationCapabilities.ReceiveProjectionV1,
                    StationCapabilities.ReceiveProjectionV1
                ]));
        Assert.Throws<InvalidOperationException>(
            () => AgentCapabilityGrantValidator.Validate(
                ["transmit-v1"]));
    }

    [Fact]
    public void RunningReleaseMetadataOverridesStaleBootstrapReleasePair()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        string root = Path.Combine(
            Path.GetTempPath(),
            $"aetherremote-running-release-{Guid.NewGuid():N}");
        string releases = Path.Combine(root, "releases");
        string identity = "aethersdr-8.8.0-acceptance.2";
        string release = Path.Combine(releases, identity);
        string agent = Path.Combine(release, "agent");
        string engine = Path.Combine(release, "station-engine");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(engine);
        string agentLink = Path.Combine(root, "agent");
        string engineLink = Path.Combine(root, "station-engine");
        Directory.CreateSymbolicLink(agentLink, agent);
        Directory.CreateSymbolicLink(engineLink, engine);
        try
        {
            AgentSettings settings = new()
            {
                ReleaseUpdateEnabled = true,
                ReleaseIdentity = "aethersdr-8.8.0-acceptance.1",
                StationEngineVersion = "8.8.0-acceptance.1"
            };

            ReconcileRunningReleaseMetadata(
                settings,
                agentLink,
                engineLink,
                releases);

            Assert.Equal(identity, settings.ReleaseIdentity);
            Assert.Equal("8.8.0-acceptance.2", settings.StationEngineVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RunningReleaseMetadataRejectsAgentEngineReleaseDisagreement()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        string root = Path.Combine(
            Path.GetTempPath(),
            $"aetherremote-running-release-{Guid.NewGuid():N}");
        string releases = Path.Combine(root, "releases");
        string agent = Path.Combine(releases, "aethersdr-8.8.0-acceptance.2", "agent");
        string engine = Path.Combine(releases, "aethersdr-8.8.0-acceptance.1", "station-engine");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(engine);
        string agentLink = Path.Combine(root, "agent");
        string engineLink = Path.Combine(root, "station-engine");
        Directory.CreateSymbolicLink(agentLink, agent);
        Directory.CreateSymbolicLink(engineLink, engine);
        try
        {
            AgentSettings settings = new() { ReleaseUpdateEnabled = true };

            Assert.Throws<InvalidOperationException>(() =>
                ReconcileRunningReleaseMetadata(
                    settings,
                    agentLink,
                    engineLink,
                    releases));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReconcileRunningReleaseMetadata(
        AgentSettings settings,
        string agentLink,
        string engineLink,
        string releaseRoot)
    {
        Type resolver = typeof(AgentSettings).Assembly.GetType(
            "AetherRemote.Agent.AgentRunningReleaseMetadata",
            throwOnError: true)!;
        MethodInfo method = resolver.GetMethod(
            "Reconcile",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "The Agent running-release metadata resolver is unavailable.");
        try
        {
            method.Invoke(null, [settings, agentLink, engineLink, releaseRoot]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    [Fact]
    public void LegacyConfiguredVersionCannotOverrideCompiledVersion()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AgentSettings.SectionName}:SoftwareVersion"] = "0.1.0"
            })
            .Build();

        AgentSettings settings = configuration
            .GetSection(AgentSettings.SectionName)
            .Get<AgentSettings>() ?? new AgentSettings();

        Assert.Equal("0.3.6", settings.SoftwareVersion);
        Assert.False(
            typeof(AgentSettings)
                .GetProperty(nameof(AgentSettings.SoftwareVersion))!
                .CanWrite);
    }
}
