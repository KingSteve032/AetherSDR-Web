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
