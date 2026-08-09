using System.Text;

namespace AetherSDR.Web.Setup;

internal sealed record InstallationInstallerFirewallRule(
    string Port,
    string Executable,
    IReadOnlyList<string> Arguments);

internal sealed record InstallationInstallerFirewallPlan(
    InstallationTopologyKind Topology,
    InstallationFirewallMode Mode,
    string SourceAssetPath,
    string GuidanceTargetPath,
    string GuidanceContent,
    IReadOnlyList<InstallationInstallerFirewallRule> Rules);

internal static class InstallationInstallerFirewallPlanComposer
{
    internal const string GuidanceAssetName = "firewall-guidance.md";
    internal const string GuidanceTarget =
        "/var/lib/aethersdr-installer/firewall/firewall-guidance.md";
    internal const string UfwExecutable = "/usr/sbin/ufw";
    internal const int MaximumGuidanceBytes = 64 * 1024;

    internal static InstallationInstallerFirewallPlan Compose(
        InstallationInstallerUbuntuMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstallationInstallerPlanAction action = RequireSingleAction(
            request.Actions,
            InstallationInstallerActionKind.WriteFirewallGuidance);
        string[] target = action.Target.Split(
            '/',
            StringSplitOptions.None);
        if (target.Length != 2 ||
            !Enum.TryParse(target[0], ignoreCase: false, out
                InstallationTopologyKind topology) ||
            !Enum.IsDefined(topology) ||
            !Enum.TryParse(target[1], ignoreCase: false, out
                InstallationFirewallMode mode) ||
            !Enum.IsDefined(mode))
        {
            throw new InvalidOperationException(
                "The exact installer firewall selection is invalid.");
        }

        string source = Path.Combine(
            request.InstallerAssetRoot,
            InstallationInstallerProxyPlanComposer.ProxyAssetDirectory.Replace(
                '/',
                Path.DirectorySeparatorChar),
            GuidanceAssetName);
        string guidance = ReadSafeGuidance(source);
        IReadOnlyList<InstallationInstallerFirewallRule> rules =
            BuildRules(request.Actions, topology, mode);
        return new(
            topology,
            mode,
            source,
            GuidanceTarget,
            guidance,
            rules);
    }

    private static IReadOnlyList<InstallationInstallerFirewallRule> BuildRules(
        IReadOnlyList<InstallationInstallerPlanAction> actions,
        InstallationTopologyKind topology,
        InstallationFirewallMode mode)
    {
        if (mode == InstallationFirewallMode.GuidanceOnly ||
            topology == InstallationTopologyKind.RemoteStationNode)
        {
            return [];
        }

        InstallationInstallerPlanAction? proxy = actions.SingleOrDefault(
            action => action.Kind ==
                InstallationInstallerActionKind.ConfigureReverseProxy);
        if (proxy is null ||
            !Enum.TryParse(
                proxy.Target,
                ignoreCase: false,
                out InstallationReverseProxyMode proxyMode) ||
            !Enum.IsDefined(proxyMode))
        {
            throw new InvalidOperationException(
                "Applied gateway firewall rules require one exact proxy mode.");
        }

        InstallationInstallerPlanAction tls = RequireSingleAction(
            actions,
            InstallationInstallerActionKind.VerifyTls);
        if (!Uri.TryCreate(tls.Target, UriKind.Absolute, out Uri? publicUri) ||
            publicUri.Scheme != Uri.UriSchemeHttps ||
            publicUri.Port == 80)
        {
            throw new InvalidOperationException(
                "Applied gateway firewall rules require one canonical HTTPS port.");
        }
        string httpsPort = publicUri.Port.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string[] ports = proxyMode == InstallationReverseProxyMode.ManagedCaddy
            ? new[] { "80", "443", httpsPort }
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [httpsPort];
        return Array.AsReadOnly(
            ports.Select(port => new InstallationInstallerFirewallRule(
                port,
                UfwExecutable,
                Array.AsReadOnly(new[]
                {
                    "allow",
                    port + "/tcp",
                    "comment",
                    "AetherSDR installer"
                }))).ToArray());
    }

    private static string ReadSafeGuidance(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Installer firewall guidance requires Linux.");
        }

        FileInfo file = new(path);
        file.Refresh();
        if (!file.Exists ||
            file.LinkTarget is not null ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.Length is < 1 or > MaximumGuidanceBytes)
        {
            throw new InvalidOperationException(
                "The reviewed firewall guidance is missing or unsafe.");
        }
        UnixFileMode fileMode = File.GetUnixFileMode(path);
        if ((fileMode &
            (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidOperationException(
                "The reviewed firewall guidance is shared-writable.");
        }

        string content = File.ReadAllText(path, Encoding.UTF8);
        if (content.Length is < 32 or > MaximumGuidanceBytes ||
            content.Any(character =>
                character == '\0' ||
                (char.IsControl(character) &&
                 character is not '\r' and not '\n' and not '\t')))
        {
            throw new InvalidOperationException(
                "The reviewed firewall guidance is invalid.");
        }
        return content.EndsWith('\n') ? content : content + "\n";
    }

    private static InstallationInstallerPlanAction RequireSingleAction(
        IReadOnlyList<InstallationInstallerPlanAction> actions,
        InstallationInstallerActionKind kind)
    {
        InstallationInstallerPlanAction[] matches =
            actions.Where(action => action.Kind == kind).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                "The exact installer plan does not contain one firewall action.");
        }
        return matches[0];
    }
}
