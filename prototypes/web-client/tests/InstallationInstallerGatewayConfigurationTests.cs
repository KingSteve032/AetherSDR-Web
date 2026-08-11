using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationInstallerGatewayConfigurationTests
{
    [Fact]
    public void LocalGatewayEnvironmentIsOwnerOnlyPlanWithoutSecret()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
            InstallationInstallerAuthenticationSelection.Local,
            clientSecretSourcePath: null);

        InstallationInstallerGatewayConfigurationPlan plan =
            InstallationInstallerGatewayConfigurationPlanComposer.Compose(
                request);

        Assert.Equal(
            "/etc/aethersdr/environment",
            plan.EnvironmentTargetPath);
        Assert.False(plan.RequiresClientSecret);
        Assert.Contains(
            "AllowedHosts=\"radio.example.org;localhost;127.0.0.1\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.Contains(
            "AllowedOrigins__0=\"https://radio.example.org\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReverseProxy__KnownProxies__0=\"127.0.0.1\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.Contains(
            "InstallationRuntime__SetupRevision=\"42\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.Contains(
            "Auth__Mode=\"Local\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.Contains(
            "Radio__Mode=\"Simulation\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.Contains(
            "Radio__AllowTransmit=\"false\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ClientSecret",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedExternalPlanContainsOnlyNonSecretConfiguration()
    {
        const string sourcePath = "/srv/private/aether-client-secret";
        InstallationInstallerAuthenticationSelection authentication = new(
            InstallationInstallerAuthenticationMode
                .CombinedMicrosoftEntraId,
            "entra-primary",
            "https://login.microsoftonline.com/tenant/v2.0",
            "application-client-id");
        InstallationInstallerUbuntuMutationRequest request = Request(
            authentication,
            sourcePath);

        InstallationInstallerGatewayConfigurationPlan plan =
            InstallationInstallerGatewayConfigurationPlanComposer.Compose(
                request);

        Assert.True(plan.RequiresClientSecret);
        Assert.Equal(
            "/var/lib/aethersdr/secrets/auth-client-secret",
            plan.ClientSecretTargetPath);
        Assert.Contains(
            "Auth__Mode=\"Combined\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.Contains(
            "Auth__ProviderType=\"EntraId\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.Contains(
            "Auth__ProviderId=\"entra-primary\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.Contains(
            "Auth__ClientSecretFile=\"/var/lib/aethersdr/secrets/auth-client-secret\"",
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            sourcePath,
            plan.RenderedEnvironment,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalSecretSourceNeverEntersPrimitivePlan()
    {
        const string sourcePath = "/srv/private/source-client-secret";
        InstallationInstallerUbuntuMutationRequest request = Request(
            new(
                InstallationInstallerAuthenticationMode.OpenIdConnect,
                "primary",
                "https://issuer.example",
                "client"),
            sourcePath);

        IReadOnlyList<InstallationInstallerUbuntuPrimitiveOperation> operations =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request);

        Assert.Contains(
            operations,
            operation =>
                operation.Kind ==
                    InstallationInstallerUbuntuPrimitiveKind
                        .InstallAuthenticationClientSecret &&
                operation.Target ==
                    "/var/lib/aethersdr/secrets/auth-client-secret" &&
                operation.Arguments.Count == 0);
        Assert.Contains(
            operations,
            operation =>
                operation.Kind ==
                    InstallationInstallerUbuntuPrimitiveKind
                        .ConfigureGatewayEnvironment &&
                operation.Target == "/etc/aethersdr/environment" &&
                operation.Arguments.Count == 0);
        Assert.DoesNotContain(
            operations.SelectMany(operation => operation.Arguments),
            argument => argument.Contains(sourcePath, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(InstallationInstallerAuthenticationMode.None)]
    [InlineData(InstallationInstallerAuthenticationMode.Local)]
    public void NonExternalModesRejectDormantProviderConfiguration(
        InstallationInstallerAuthenticationMode mode)
    {
        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerGatewayConfigurationPlanComposer
                .NormalizeAndValidate(
                    new(
                        mode,
                        "dormant",
                        "https://issuer.example",
                        "client")));
    }

    [Fact]
    public void ExternalModeRequiresCanonicalHttpsProvider()
    {
        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerGatewayConfigurationPlanComposer
                .NormalizeAndValidate(
                    new(
                        InstallationInstallerAuthenticationMode
                            .OpenIdConnect,
                        "primary",
                        "http://issuer.example",
                        "client")));
        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerGatewayConfigurationPlanComposer
                .NormalizeAndValidate(
                    new(
                        InstallationInstallerAuthenticationMode
                            .OpenIdConnect,
                        "Primary",
                        "https://issuer.example",
                        "client")));
    }

    private static InstallationInstallerUbuntuMutationRequest Request(
        InstallationInstallerAuthenticationSelection authentication,
        string? clientSecretSourcePath)
    {
        InstallationInstallerGatewayConfiguration configuration = new(
            SetupRevision: 42,
            InstallationTopologyKind.PersonalSingleStation,
            "https://radio.example.org",
            InstallTransmitSupport: false,
            InstallationReverseProxyMode.ManagedCaddy,
            authentication);
        List<InstallationInstallerPlanAction> actions =
        [
            new(
                1,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64")
        ];
        if (authentication.UsesExternalProvider)
        {
            actions.Add(new(
                actions.Count + 1,
                InstallationInstallerActionKind
                    .InstallAuthenticationClientSecret,
                InstallationInstallerGatewayConfigurationPlanComposer
                    .ClientSecretTargetPath));
        }
        actions.Add(new(
            actions.Count + 1,
            InstallationInstallerActionKind.ConfigureGatewayEnvironment,
            InstallationInstallerGatewayConfigurationPlanComposer
                .EnvironmentTargetPath));

        return new(
            new string('a', 64),
            setupRevision: 42,
            releaseIdentity: "2026.8.0",
            InstallationInstallerArchitecture.LinuxX64,
            immutableStagingPath:
                "/var/lib/aethersdr-installer/releases/2026.8.0",
            targetReleasePath: "/opt/aethersdr/releases/2026.8.0",
            repair: false,
            actions,
            gatewayConfiguration: configuration,
            authenticationClientSecretSourcePath: clientSecretSourcePath);
    }
}
