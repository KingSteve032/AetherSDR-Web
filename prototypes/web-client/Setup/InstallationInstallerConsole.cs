using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherSDR.Web.Setup;

public enum InstallationInstallerConsoleCommandKind
{
    None = 0,
    Plan = 1,
    Validate = 2,
    Apply = 3,
    Repair = 4
}

public sealed record InstallationInstallerConsoleCommandLine(
    InstallationInstallerConsoleCommandKind Command,
    InstallationInstallerArchitecture? Architecture,
    InstallationReverseProxyMode? ReverseProxyMode,
    InstallationFirewallMode FirewallMode,
    string ReleaseIdentity,
    string ConfirmedPlanId,
    string BundleDirectory,
    int? ConfigurationSchemaVersion,
    int? ProtocolVersion,
    IReadOnlyList<string> ApplicationArguments);

public static class InstallationInstallerConsoleCommandParser
{
    public const string PlanSwitch = "--installation-installer-plan";
    public const string ValidateSwitch = "--installation-installer-validate";
    public const string ApplySwitch = "--installation-installer-apply";
    public const string RepairSwitch = "--installation-installer-repair";
    public const string ArchitectureSwitch = "--installation-architecture";
    public const string ReverseProxySwitch = "--installation-reverse-proxy";
    public const string ReleaseSwitch = "--installation-release";
    public const string FirewallSwitch = "--installation-firewall";
    public const string ConfirmPlanSwitch = "--confirm-installation-plan";
    public const string BundleSwitch = "--installation-bundle";
    public const string ConfigurationSchemaSwitch =
        "--installation-configuration-schema";
    public const string ProtocolVersionSwitch =
        "--installation-protocol-version";

    public static InstallationInstallerConsoleCommandLine Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        InstallationInstallerConsoleCommandKind command =
            InstallationInstallerConsoleCommandKind.None;
        InstallationInstallerArchitecture? architecture = null;
        InstallationReverseProxyMode? proxy = null;
        InstallationFirewallMode firewall =
            InstallationFirewallMode.GuidanceOnly;
        bool firewallSet = false;
        string release = string.Empty;
        string confirmedPlan = string.Empty;
        string bundleDirectory = string.Empty;
        int? configurationSchemaVersion = null;
        int? protocolVersion = null;
        List<string> applicationArguments = [];

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case PlanSwitch:
                    SetCommand(ref command, InstallationInstallerConsoleCommandKind.Plan);
                    break;
                case ValidateSwitch:
                    SetCommand(ref command, InstallationInstallerConsoleCommandKind.Validate);
                    break;
                case ApplySwitch:
                    SetCommand(ref command, InstallationInstallerConsoleCommandKind.Apply);
                    break;
                case RepairSwitch:
                    SetCommand(ref command, InstallationInstallerConsoleCommandKind.Repair);
                    break;
                case ArchitectureSwitch:
                    RequireUnset(architecture is not null, argument);
                    architecture = ParseArchitecture(
                        RequireValue(arguments, ref index, argument));
                    break;
                case ReverseProxySwitch:
                    RequireUnset(proxy is not null, argument);
                    proxy = ParseProxy(
                        RequireValue(arguments, ref index, argument));
                    break;
                case FirewallSwitch:
                    RequireUnset(firewallSet, argument);
                    firewall = ParseFirewall(
                        RequireValue(arguments, ref index, argument));
                    firewallSet = true;
                    break;
                case ReleaseSwitch:
                    RequireUnset(!string.IsNullOrEmpty(release), argument);
                    release = InstallationReleaseIdentity.Parse(
                        RequireValue(arguments, ref index, argument));
                    break;
                case ConfirmPlanSwitch:
                    RequireUnset(!string.IsNullOrEmpty(confirmedPlan), argument);
                    confirmedPlan = ParsePlanId(
                        RequireValue(arguments, ref index, argument));
                    break;
                case BundleSwitch:
                    RequireUnset(!string.IsNullOrEmpty(bundleDirectory), argument);
                    bundleDirectory = Path.GetFullPath(
                        RequireValue(arguments, ref index, argument));
                    break;
                case ConfigurationSchemaSwitch:
                    RequireUnset(configurationSchemaVersion is not null, argument);
                    configurationSchemaVersion = ParsePositiveInteger(
                        RequireValue(arguments, ref index, argument),
                        argument);
                    break;
                case ProtocolVersionSwitch:
                    RequireUnset(protocolVersion is not null, argument);
                    protocolVersion = ParsePositiveInteger(
                        RequireValue(arguments, ref index, argument),
                        argument);
                    break;
                default:
                    applicationArguments.Add(argument);
                    break;
            }
        }

        if (command == InstallationInstallerConsoleCommandKind.None)
        {
            if (architecture is not null ||
                proxy is not null ||
                firewallSet ||
                !string.IsNullOrEmpty(release) ||
                !string.IsNullOrEmpty(confirmedPlan) ||
                !string.IsNullOrEmpty(bundleDirectory) ||
                configurationSchemaVersion is not null ||
                protocolVersion is not null)
            {
                throw new InvalidOperationException(
                    "Installer options require one installer command.");
            }
        }
        else if (architecture is null ||
                 proxy is null ||
                 string.IsNullOrEmpty(release))
        {
            throw new InvalidOperationException(
                "An installer command requires architecture, reverse-proxy mode, and release identity.");
        }

        bool mutation =
            command is InstallationInstallerConsoleCommandKind.Apply or
                InstallationInstallerConsoleCommandKind.Repair;
        if (mutation &&
            (string.IsNullOrEmpty(confirmedPlan) ||
             string.IsNullOrEmpty(bundleDirectory) ||
             configurationSchemaVersion is null ||
             protocolVersion is null))
        {
            throw new InvalidOperationException(
                "Installer mutation requires plan confirmation, signed bundle, configuration schema, and protocol version.");
        }
        if (!mutation &&
            (!string.IsNullOrEmpty(confirmedPlan) ||
             !string.IsNullOrEmpty(bundleDirectory) ||
             configurationSchemaVersion is not null ||
             protocolVersion is not null))
        {
            throw new InvalidOperationException(
                "Mutation confirmation and release payload options are valid only for apply or repair.");
        }

        return new(
            command,
            architecture,
            proxy,
            firewall,
            release,
            confirmedPlan,
            bundleDirectory,
            configurationSchemaVersion,
            protocolVersion,
            [.. applicationArguments]);
    }

    private static void SetCommand(
        ref InstallationInstallerConsoleCommandKind current,
        InstallationInstallerConsoleCommandKind candidate)
    {
        if (current != InstallationInstallerConsoleCommandKind.None)
        {
            throw new InvalidOperationException(
                "Only one installer console command may be requested.");
        }
        current = candidate;
    }

    private static void RequireUnset(bool alreadySet, string option)
    {
        if (alreadySet)
        {
            throw new InvalidOperationException(
                $"Installer option '{option}' was provided more than once.");
        }
    }

    private static string RequireValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        if (index + 1 >= arguments.Count ||
            string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            throw new InvalidOperationException(
                $"Installer option '{option}' requires one value.");
        }
        return arguments[++index].Trim();
    }

    private static InstallationInstallerArchitecture ParseArchitecture(
        string value) =>
        value.ToLowerInvariant() switch
        {
            "linux-x64" => InstallationInstallerArchitecture.LinuxX64,
            "linux-arm64" => InstallationInstallerArchitecture.LinuxArm64,
            _ => throw new InvalidOperationException(
                $"Unsupported installer architecture '{value}'.")
        };

    private static InstallationReverseProxyMode ParseProxy(string value) =>
        value.ToLowerInvariant() switch
        {
            "none" => InstallationReverseProxyMode.None,
            "existing-caddy" => InstallationReverseProxyMode.ExistingCaddy,
            "existing-nginx" => InstallationReverseProxyMode.ExistingNginx,
            "existing-other" => InstallationReverseProxyMode.ExistingOther,
            "managed-caddy" => InstallationReverseProxyMode.ManagedCaddy,
            "lan-internal-certificate" =>
                InstallationReverseProxyMode.LanInternalCertificate,
            _ => throw new InvalidOperationException(
                $"Unsupported installer reverse-proxy mode '{value}'.")
        };

    private static InstallationFirewallMode ParseFirewall(string value) =>
        value.ToLowerInvariant() switch
        {
            "guidance" => InstallationFirewallMode.GuidanceOnly,
            "apply-ufw" => InstallationFirewallMode.ApplyUfwRules,
            _ => throw new InvalidOperationException(
                $"Unsupported installer firewall mode '{value}'.")
        };

    private static int ParsePositiveInteger(string value, string option)
    {
        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsed) ||
            parsed < 1)
        {
            throw new InvalidOperationException(
                $"Installer option '{option}' requires one positive integer.");
        }
        return parsed;
    }

    private static string ParsePlanId(string value)
    {
        string normalized = value.ToLowerInvariant();
        if (normalized.Length != 64 ||
            normalized.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException(
                "Installer plan confirmation requires one SHA-256 plan identity.");
        }
        return normalized;
    }
}

public sealed class InstallationInstallerConsole
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private readonly InstallationInstallerCoordinator m_coordinator;

    public InstallationInstallerConsole(
        InstallationInstallerCoordinator coordinator)
    {
        m_coordinator = coordinator ??
            throw new ArgumentNullException(nameof(coordinator));
    }

    public async Task<int> ExecuteAsync(
        InstallationInstallerConsoleCommandLine commandLine,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(output);
        if (commandLine.Command == InstallationInstallerConsoleCommandKind.None)
        {
            throw new InvalidOperationException(
                "An installer console command is required.");
        }

        InstallationInstallerPlanReport plan =
            await m_coordinator.PlanAsync(
                new InstallationInstallerSelection(
                    commandLine.Architecture ??
                        throw new InvalidOperationException(
                            "Installer architecture is required."),
                    commandLine.ReverseProxyMode ??
                        throw new InvalidOperationException(
                            "Installer reverse-proxy mode is required."),
                    commandLine.ReleaseIdentity,
                    commandLine.FirewallMode),
                cancellationToken);

        if (commandLine.Command is
                InstallationInstallerConsoleCommandKind.Apply or
                InstallationInstallerConsoleCommandKind.Repair &&
            !string.Equals(
                commandLine.ConfirmedPlanId,
                plan.PlanId,
                StringComparison.Ordinal))
        {
            await WriteAsync(
                output,
                new
                {
                    outcome = "rejected",
                    code = "plan-confirmation-mismatch",
                    planId = plan.PlanId,
                    mutationAttempted = false
                });
            return 2;
        }

        object result = commandLine.Command switch
        {
            InstallationInstallerConsoleCommandKind.Plan => plan,
            InstallationInstallerConsoleCommandKind.Validate =>
                await m_coordinator.ValidateAsync(plan, cancellationToken),
            InstallationInstallerConsoleCommandKind.Apply =>
                await m_coordinator.ApplyAsync(plan, cancellationToken),
            InstallationInstallerConsoleCommandKind.Repair =>
                await m_coordinator.RepairAsync(plan, cancellationToken),
            _ => throw new InvalidOperationException(
                "The installer console command is unsupported.")
        };
        await WriteAsync(output, result);
        return result is InstallationInstallerOperationResult operation &&
               operation.Outcome is
                   InstallationInstallerOperationOutcome.Rejected or
                   InstallationInstallerOperationOutcome.Disabled or
                   InstallationInstallerOperationOutcome.ReconciliationRequired
            ? 2
            : 0;
    }

    private static Task WriteAsync(TextWriter output, object value) =>
        output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
}
