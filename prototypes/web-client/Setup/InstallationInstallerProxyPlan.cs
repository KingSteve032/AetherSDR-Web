using System.Net;
using System.Text;

namespace AetherSDR.Web.Setup;

internal enum InstallationInstallerProxyOwnership
{
    OperatorManaged = 1,
    InstallerManaged = 2
}

internal sealed record InstallationInstallerProxyPlan(
    InstallationReverseProxyMode Mode,
    InstallationInstallerProxyOwnership Ownership,
    string PublicHost,
    int HttpsPort,
    string SourceAssetPath,
    string TargetPath,
    string RenderedContent,
    bool InternalCertificate,
    bool RequiresOperatorCertificatePaths,
    bool RequiresCaddyPackage,
    string ValidationExecutable,
    IReadOnlyList<string> ValidationArguments);

internal static class InstallationInstallerProxyPlanComposer
{
    internal const string ProxyAssetDirectory = "installer/proxy";
    internal const string CaddyTemplateName = "Caddyfile.template";
    internal const string NginxTemplateName = "nginx-aethersdr.conf.template";
    internal const string OtherRequirementsName =
        "existing-proxy-requirements.md";
    internal const string ManagedCaddyTarget = "/etc/caddy/Caddyfile";
    internal const string CaddyExecutable = "/usr/bin/caddy";
    internal const int MaximumTemplateBytes = 64 * 1024;

    internal static InstallationInstallerProxyPlan Compose(
        InstallationInstallerUbuntuMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InstallationInstallerPlanAction proxyAction = RequireSingleAction(
            request.Actions,
            InstallationInstallerActionKind.ConfigureReverseProxy);
        InstallationInstallerPlanAction tlsAction = RequireSingleAction(
            request.Actions,
            InstallationInstallerActionKind.VerifyTls);
        InstallationReverseProxyMode mode =
            Enum.Parse<InstallationReverseProxyMode>(
                proxyAction.Target,
                ignoreCase: false);
        Uri publicUri = RequirePublicHttpsUri(tlsAction.Target);
        string host = publicUri.IdnHost;
        if (publicUri.Port == 80)
        {
            throw new InvalidOperationException(
                "The installer proxy cannot terminate HTTPS on the reserved HTTP challenge port.");
        }
        string authority = publicUri.Authority;
        int httpsPort = publicUri.Port;
        string sourceRoot = Path.Combine(
            request.InstallerAssetRoot,
            ProxyAssetDirectory.Replace(
                '/',
                Path.DirectorySeparatorChar));
        string stateRoot = "/var/lib/aethersdr-installer/proxy";

        string source;
        string target;
        InstallationInstallerProxyOwnership ownership;
        bool internalCertificate = false;
        bool operatorCertificatePaths = false;
        bool requiresCaddy = false;
        string validationExecutable = string.Empty;
        IReadOnlyList<string> validationArguments = [];

        switch (mode)
        {
            case InstallationReverseProxyMode.ExistingCaddy:
                source = Path.Combine(sourceRoot, CaddyTemplateName);
                target = Path.Combine(stateRoot, "aethersdr.Caddyfile");
                ownership = InstallationInstallerProxyOwnership.OperatorManaged;
                break;
            case InstallationReverseProxyMode.ExistingNginx:
                source = Path.Combine(sourceRoot, NginxTemplateName);
                target = Path.Combine(stateRoot, "aethersdr.nginx.conf");
                ownership = InstallationInstallerProxyOwnership.OperatorManaged;
                operatorCertificatePaths = true;
                break;
            case InstallationReverseProxyMode.ExistingOther:
                source = Path.Combine(sourceRoot, OtherRequirementsName);
                target = Path.Combine(
                    stateRoot,
                    "existing-proxy-requirements.md");
                ownership = InstallationInstallerProxyOwnership.OperatorManaged;
                break;
            case InstallationReverseProxyMode.ManagedCaddy:
                RequirePublicDnsHost(host);
                source = Path.Combine(sourceRoot, CaddyTemplateName);
                target = ManagedCaddyTarget;
                ownership = InstallationInstallerProxyOwnership.InstallerManaged;
                requiresCaddy = true;
                validationExecutable = CaddyExecutable;
                validationArguments =
                [
                    "validate",
                    "--config",
                    ManagedCaddyTarget,
                    "--adapter",
                    "caddyfile"
                ];
                break;
            case InstallationReverseProxyMode.LanInternalCertificate:
                source = Path.Combine(sourceRoot, CaddyTemplateName);
                target = ManagedCaddyTarget;
                ownership = InstallationInstallerProxyOwnership.InstallerManaged;
                internalCertificate = true;
                requiresCaddy = true;
                validationExecutable = CaddyExecutable;
                validationArguments =
                [
                    "validate",
                    "--config",
                    ManagedCaddyTarget,
                    "--adapter",
                    "caddyfile"
                ];
                break;
            default:
                throw new InvalidOperationException(
                    "The installer proxy mode is unsupported for a gateway.");
        }

        string template = ReadSafeAsset(source);
        string rendered = Render(
            template,
            mode,
            host,
            authority,
            httpsPort,
            internalCertificate);
        return new(
            mode,
            ownership,
            host,
            httpsPort,
            source,
            target,
            rendered,
            internalCertificate,
            operatorCertificatePaths,
            requiresCaddy,
            validationExecutable,
            Array.AsReadOnly(validationArguments.ToArray()));
    }

    private static string Render(
        string template,
        InstallationReverseProxyMode mode,
        string host,
        string authority,
        int httpsPort,
        bool internalCertificate)
    {
        string rendered = template
            .Replace(
                "{{PUBLIC_HOST}}",
                host,
                StringComparison.Ordinal)
            .Replace(
                "{{PUBLIC_AUTHORITY}}",
                authority,
                StringComparison.Ordinal)
            .Replace(
                "{{HTTPS_PORT}}",
                httpsPort.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        if (mode is InstallationReverseProxyMode.ExistingCaddy or
            InstallationReverseProxyMode.ManagedCaddy or
            InstallationReverseProxyMode.LanInternalCertificate)
        {
            rendered = rendered.Replace(
                "{{TLS_DIRECTIVE}}",
                internalCertificate ? "tls internal" : string.Empty,
                StringComparison.Ordinal);
        }

        if (mode != InstallationReverseProxyMode.ExistingNginx &&
            rendered.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The reviewed proxy asset contains an unresolved token.");
        }
        if (rendered.Length is < 32 or > MaximumTemplateBytes ||
            rendered.Any(character =>
                character == '\0' ||
                (char.IsControl(character) &&
                 character is not '\r' and not '\n' and not '\t')))
        {
            throw new InvalidOperationException(
                "The rendered proxy asset is invalid.");
        }
        return rendered.EndsWith('\n')
            ? rendered
            : rendered + "\n";
    }

    private static Uri RequirePublicHttpsUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(uri.IdnHost) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                "The installer proxy requires one canonical HTTPS origin.");
        }
        return uri;
    }

    private static void RequirePublicDnsHost(string host)
    {
        if (IPAddress.TryParse(host, out _) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            !host.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Managed public Caddy requires a fully qualified DNS host.");
        }
    }

    private static string ReadSafeAsset(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Installer proxy assets require Linux.");
        }
        FileInfo file = new(path);
        file.Refresh();
        if (!file.Exists ||
            file.LinkTarget is not null ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.Length is < 1 or > MaximumTemplateBytes)
        {
            throw new InvalidOperationException(
                "The reviewed proxy asset is missing or unsafe.");
        }
        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode &
            (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidOperationException(
                "The reviewed proxy asset is shared-writable.");
        }
        return File.ReadAllText(path, Encoding.UTF8);
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
                "The exact installer plan does not contain one required proxy action.");
        }
        return matches[0];
    }
}
