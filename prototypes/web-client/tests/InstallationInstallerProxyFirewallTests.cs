using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationInstallerProxyFirewallTests
{
    [Fact]
    public void ManagedCaddyRendersExactPublicHttpsConfiguration()
    {
        InstallationInstallerProxyPlan plan =
            InstallationInstallerProxyPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.ManagedCaddy,
                    "https://radio.example.test/"));

        Assert.Equal(
            InstallationInstallerProxyOwnership.InstallerManaged,
            plan.Ownership);
        Assert.Equal("/etc/caddy/Caddyfile", plan.TargetPath);
        Assert.Equal("radio.example.test", plan.PublicHost);
        Assert.Equal(443, plan.HttpsPort);
        Assert.True(plan.RequiresCaddyPackage);
        Assert.False(plan.InternalCertificate);
        Assert.Contains("radio.example.test {", plan.RenderedContent);
        Assert.Contains(
            "reverse_proxy 127.0.0.1:5080",
            plan.RenderedContent);
        Assert.DoesNotContain(
            "stream_close_delay",
            plan.RenderedContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain("{{", plan.RenderedContent);
        Assert.Equal("/usr/bin/caddy", plan.ValidationExecutable);
        Assert.Equal(
            ["validate", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"],
            plan.ValidationArguments);
    }

    [Fact]
    public void LanCaddyRequiresExplicitInternalCertificate()
    {
        InstallationInstallerProxyPlan plan =
            InstallationInstallerProxyPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.LanInternalCertificate,
                    "https://aethersdr.lan/"));

        Assert.True(plan.InternalCertificate);
        Assert.Contains("tls internal", plan.RenderedContent);
        Assert.DoesNotContain("http://aethersdr.lan", plan.RenderedContent);
    }

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://localhost/")]
    [InlineData("https://single-label/")]
    public void ManagedPublicCaddyRejectsNonDnsPublicHost(string publicUrl)
    {
        Assert.Throws<InvalidOperationException>(() =>
            InstallationInstallerProxyPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.ManagedCaddy,
                    publicUrl)));
    }

    [Fact]
    public void ExistingNginxProducesOperatorArtifactWithoutTakingOwnership()
    {
        InstallationInstallerProxyPlan plan =
            InstallationInstallerProxyPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.ExistingNginx,
                    "https://radio.example.test/"));

        Assert.Equal(
            InstallationInstallerProxyOwnership.OperatorManaged,
            plan.Ownership);
        Assert.Equal(
            "/var/lib/aethersdr-installer/proxy/aethersdr.nginx.conf",
            plan.TargetPath);
        Assert.True(plan.RequiresOperatorCertificatePaths);
        Assert.Contains(
            "ssl_certificate {{TLS_CERTIFICATE_PATH}};",
            plan.RenderedContent);
        Assert.Contains(
            "proxy_set_header Upgrade $http_upgrade;",
            plan.RenderedContent);
        Assert.False(plan.RequiresCaddyPackage);
    }

    [Fact]
    public void NonDefaultHttpsPortFlowsThroughProxyAndFirewallPlans()
    {
        InstallationInstallerProxyPlan caddy =
            InstallationInstallerProxyPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.ManagedCaddy,
                    "https://radio.example.test:8443/"));
        InstallationInstallerProxyPlan nginx =
            InstallationInstallerProxyPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.ExistingNginx,
                    "https://radio.example.test:8443/"));
        InstallationInstallerFirewallPlan firewall =
            InstallationInstallerFirewallPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.ManagedCaddy,
                    "https://radio.example.test:8443/",
                    InstallationFirewallMode.ApplyUfwRules));

        Assert.Equal(8443, caddy.HttpsPort);
        Assert.Contains(
            "radio.example.test:8443 {",
            caddy.RenderedContent);
        Assert.Contains("listen 8443 ssl;", nginx.RenderedContent);
        Assert.Equal(
            ["80", "443", "8443"],
            firewall.Rules.Select(rule => rule.Port));
    }

    [Fact]
    public void HttpsPortEightyIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            InstallationInstallerProxyPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.LanInternalCertificate,
                    "https://aethersdr.lan:80/")));
        Assert.Throws<InvalidOperationException>(() =>
            InstallationInstallerFirewallPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.ExistingNginx,
                    "https://radio.example.test:80/",
                    InstallationFirewallMode.ApplyUfwRules)));
    }

    [Fact]
    public void GuidanceModeNeverComposesFirewallMutation()
    {
        InstallationInstallerFirewallPlan plan =
            InstallationInstallerFirewallPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.ManagedCaddy,
                    "https://radio.example.test/",
                    InstallationFirewallMode.GuidanceOnly));

        Assert.Empty(plan.Rules);
        Assert.Contains(
            "Never expose the loopback gateway port 5080.",
            plan.GuidanceContent);
    }

    [Fact]
    public void ManagedPublicCaddyComposesOnlyReviewedUfwPorts()
    {
        InstallationInstallerFirewallPlan plan =
            InstallationInstallerFirewallPlanComposer.Compose(
                Request(
                    InstallationReverseProxyMode.ManagedCaddy,
                    "https://radio.example.test/",
                    InstallationFirewallMode.ApplyUfwRules));

        Assert.Equal(["80", "443"], plan.Rules.Select(rule => rule.Port));
        Assert.All(
            plan.Rules,
            rule =>
            {
                Assert.Equal("/usr/sbin/ufw", rule.Executable);
                Assert.Equal("allow", rule.Arguments[0]);
                Assert.DoesNotContain("enable", rule.Arguments);
                Assert.DoesNotContain("delete", rule.Arguments);
                Assert.DoesNotContain("reset", rule.Arguments);
                Assert.DoesNotContain("default", rule.Arguments);
            });
    }

    [Fact]
    public void RemoteStationNodeNeverReceivesInboundFirewallRules()
    {
        InstallationInstallerFirewallPlan plan =
            InstallationInstallerFirewallPlanComposer.Compose(
                Request(
                    proxyMode: null,
                    "https://radio.example.test/",
                    InstallationFirewallMode.ApplyUfwRules,
                    InstallationTopologyKind.RemoteStationNode));

        Assert.Empty(plan.Rules);
    }

    private static InstallationInstallerUbuntuMutationRequest Request(
        InstallationReverseProxyMode? proxyMode,
        string publicUrl,
        InstallationFirewallMode firewallMode =
            InstallationFirewallMode.GuidanceOnly,
        InstallationTopologyKind topology =
            InstallationTopologyKind.PersonalSingleStation)
    {
        List<InstallationInstallerPlanAction> actions = [];
        if (proxyMode is not null)
        {
            actions.Add(new(
                actions.Count + 1,
                InstallationInstallerActionKind.ConfigureReverseProxy,
                proxyMode.Value.ToString()));
        }
        actions.Add(new(
            actions.Count + 1,
            InstallationInstallerActionKind.VerifyTls,
            publicUrl));
        actions.Add(new(
            actions.Count + 1,
            InstallationInstallerActionKind.WriteFirewallGuidance,
            $"{topology}/{firewallMode}"));

        return new(
            new string('a', 64),
            setupRevision: 7,
            releaseIdentity: "2026.8.0",
            InstallationInstallerArchitecture.LinuxX64,
            immutableStagingPath: "/var/lib/aethersdr/.release-staging/exact",
            targetReleasePath: "/opt/aethersdr/releases/2026.8.0",
            repair: false,
            actions,
            installerAssetRoot: AssetRoot());
    }

    private static string AssetRoot() =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "deploy"));
}
