using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AetherSDR.Web.Setup;

public enum InstallationInstallerArchitecture
{
    LinuxX64 = 1,
    LinuxArm64 = 2
}

public enum InstallationReverseProxyMode
{
    None = 1,
    ExistingCaddy = 2,
    ExistingNginx = 3,
    ExistingOther = 4,
    ManagedCaddy = 5,
    LanInternalCertificate = 6
}

public enum InstallationFirewallMode
{
    GuidanceOnly = 1,
    ApplyUfwRules = 2
}

public enum InstallationInstallerActionKind
{
    EnsureServiceUser = 1,
    EnsureDirectory = 2,
    InstallVerifiedRelease = 3,
    InstallSystemdUnit = 4,
    ConfigureReverseProxy = 5,
    VerifyTls = 6,
    WriteFirewallGuidance = 7,
    ReloadSystemd = 8,
    ActivateInitialRelease = 9,
    ActivateSystemdUnit = 10,
    VerifyHealth = 11,
    TrustInternalCertificate = 12
}

public sealed record InstallationInstallerSelection(
    InstallationInstallerArchitecture Architecture,
    InstallationReverseProxyMode ReverseProxyMode,
    string ReleaseIdentity,
    InstallationFirewallMode FirewallMode =
        InstallationFirewallMode.GuidanceOnly);

public sealed record InstallationInstallerPlanAction(
    int Order,
    InstallationInstallerActionKind Kind,
    string Target);

public sealed record InstallationInstallerPlanReport(
    int SchemaVersion,
    long SetupRevision,
    DateTimeOffset SetupCreatedAt,
    DateTimeOffset SetupUpdatedAt,
    InstallationTopologyKind Topology,
    string CanonicalPublicUrl,
    InstallationInstallerArchitecture Architecture,
    InstallationReverseProxyMode ReverseProxyMode,
    InstallationFirewallMode FirewallMode,
    string ReleaseIdentity,
    bool InstallTransmitSupport,
    string PlanId,
    IReadOnlyList<string> ServiceUsers,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Services,
    IReadOnlyList<InstallationInstallerPlanAction> Actions)
{
    internal InstallationInstallerPlan? Plan { get; init; }
}

internal sealed class InstallationInstallerPlan
{
    private readonly IReadOnlyList<string> m_serviceUsers;
    private readonly IReadOnlyList<string> m_directories;
    private readonly IReadOnlyList<string> m_services;
    private readonly IReadOnlyList<InstallationInstallerPlanAction> m_actions;

    internal InstallationInstallerPlan(
        InstallationSetupState state,
        InstallationTopologyKind topology,
        string canonicalPublicUrl,
        InstallationInstallerSelection selection,
        IReadOnlyList<string> serviceUsers,
        IReadOnlyList<string> directories,
        IReadOnlyList<string> services,
        IReadOnlyList<InstallationInstallerPlanAction> actions,
        string planId)
    {
        State = state;
        Topology = topology;
        CanonicalPublicUrl = canonicalPublicUrl;
        Selection = selection;
        m_serviceUsers = Array.AsReadOnly(serviceUsers.ToArray());
        m_directories = Array.AsReadOnly(directories.ToArray());
        m_services = Array.AsReadOnly(services.ToArray());
        m_actions = Array.AsReadOnly(actions.ToArray());
        PlanId = planId;
    }

    internal InstallationSetupState State { get; }

    internal InstallationTopologyKind Topology { get; }

    internal string CanonicalPublicUrl { get; }

    internal InstallationInstallerSelection Selection { get; }

    internal IReadOnlyList<string> ServiceUsers => m_serviceUsers;

    internal IReadOnlyList<string> Directories => m_directories;

    internal IReadOnlyList<string> Services => m_services;

    internal IReadOnlyList<InstallationInstallerPlanAction> Actions => m_actions;

    internal string PlanId { get; }
}

public static class InstallationInstallerPlanComposer
{
    public const int CurrentPlanSchemaVersion = 5;

    public static InstallationInstallerPlanReport Compose(
        InstallationSetupState state,
        InstallationInstallerSelection selection)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(selection);
        InstallationSetupStateValidator.Validate(state);
        ValidateSelection(selection);

        if (state.Lock.Mode != InstallationSetupLockMode.Claimed ||
            state.LastCompletedStep < InstallationSetupStep.TransmitSupport)
        {
            throw new InvalidOperationException(
                "An installer plan requires claimed setup state completed through " +
                "the transmit-support choice.");
        }

        InstallationTopologyKind topology = state.Topology ??
            throw new InvalidOperationException(
                "An installer plan requires an installation topology.");
        InstallationTopologyProfile profile =
            InstallationTopologyProfile.For(topology);
        ValidateProxySelection(profile, selection.ReverseProxyMode);
        InstallationPaths paths = state.Paths ??
            throw new InvalidOperationException(
                "An installer plan requires installation paths.");
        InstallationPaths.Validate(paths);
        string canonicalPublicUrl =
            CanonicalPublicUrl.Parse(state.CanonicalPublicUrl).Value;
        string releaseIdentity =
            InstallationReleaseIdentity.Parse(selection.ReleaseIdentity);
        InstallationInstallerSelection normalizedSelection =
            selection with { ReleaseIdentity = releaseIdentity };

        string[] users = BuildUsers(profile);
        string[] directories = BuildDirectories(paths, profile);
        string[] services = BuildServices(profile);
        InstallationInstallerPlanAction[] actions = BuildActions(
            profile,
            normalizedSelection,
            canonicalPublicUrl,
            users,
            directories,
            services);
        string planId = ComputePlanId(
            state,
            topology,
            canonicalPublicUrl,
            normalizedSelection,
            users,
            directories,
            services,
            actions);

        InstallationInstallerPlan plan = new(
            state,
            topology,
            canonicalPublicUrl,
            normalizedSelection,
            users,
            directories,
            services,
            actions,
            planId);
        return ToReport(plan);
    }

    internal static InstallationInstallerPlan RequireExactPlan(
        InstallationInstallerPlanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        InstallationInstallerPlan plan = report.Plan ??
            throw new InvalidOperationException(
                "An installer operation requires its retained exact plan.");

        InstallationInstallerPlanReport expected = ToReport(plan);
        if (report.SchemaVersion != expected.SchemaVersion ||
            report.SetupRevision != expected.SetupRevision ||
            report.SetupCreatedAt != expected.SetupCreatedAt ||
            report.SetupUpdatedAt != expected.SetupUpdatedAt ||
            report.Topology != expected.Topology ||
            !string.Equals(
                report.CanonicalPublicUrl,
                expected.CanonicalPublicUrl,
                StringComparison.Ordinal) ||
            report.Architecture != expected.Architecture ||
            report.ReverseProxyMode != expected.ReverseProxyMode ||
            report.FirewallMode != expected.FirewallMode ||
            !string.Equals(
                report.ReleaseIdentity,
                expected.ReleaseIdentity,
                StringComparison.Ordinal) ||
            report.InstallTransmitSupport != expected.InstallTransmitSupport ||
            !string.Equals(report.PlanId, expected.PlanId, StringComparison.Ordinal) ||
            !report.ServiceUsers.SequenceEqual(
                expected.ServiceUsers,
                StringComparer.Ordinal) ||
            !report.Directories.SequenceEqual(
                expected.Directories,
                StringComparer.Ordinal) ||
            !report.Services.SequenceEqual(
                expected.Services,
                StringComparer.Ordinal) ||
            !report.Actions.SequenceEqual(expected.Actions))
        {
            throw new InvalidOperationException(
                "The installer plan summary does not match its retained exact plan.");
        }

        return plan;
    }

    private static InstallationInstallerPlanReport ToReport(
        InstallationInstallerPlan plan) =>
        new(
            CurrentPlanSchemaVersion,
            plan.State.Revision,
            plan.State.CreatedAt,
            plan.State.UpdatedAt,
            plan.Topology,
            plan.CanonicalPublicUrl,
            plan.Selection.Architecture,
            plan.Selection.ReverseProxyMode,
            plan.Selection.FirewallMode,
            plan.Selection.ReleaseIdentity,
            plan.State.InstallTransmitSupport,
            plan.PlanId,
            plan.ServiceUsers,
            plan.Directories,
            plan.Services,
            plan.Actions)
        {
            Plan = plan
        };

    private static void ValidateSelection(
        InstallationInstallerSelection selection)
    {
        if (!Enum.IsDefined(selection.Architecture) ||
            !Enum.IsDefined(selection.ReverseProxyMode) ||
            !Enum.IsDefined(selection.FirewallMode))
        {
            throw new InvalidOperationException(
                "The installer selection contains an unsupported value.");
        }
        _ = InstallationReleaseIdentity.Parse(selection.ReleaseIdentity);
    }

    private static void ValidateProxySelection(
        InstallationTopologyProfile profile,
        InstallationReverseProxyMode mode)
    {
        if (profile.GatewayRunsHere && mode == InstallationReverseProxyMode.None)
        {
            throw new InvalidOperationException(
                "A gateway installer plan requires an explicit reverse-proxy and " +
                "TLS mode.");
        }
        if (!profile.GatewayRunsHere && mode != InstallationReverseProxyMode.None)
        {
            throw new InvalidOperationException(
                "A remote station node cannot configure a gateway reverse proxy.");
        }
    }

    private static string[] BuildUsers(InstallationTopologyProfile profile)
    {
        List<string> users = [];
        if (profile.GatewayRunsHere)
        {
            users.Add("aethersdr");
        }
        if (profile.BrokerRunsHere ||
            profile.StationEngineRunsHere ||
            profile.AgentRunsHere)
        {
            users.Add("aetherremote");
        }
        return users.ToArray();
    }

    private static string[] BuildDirectories(
        InstallationPaths paths,
        InstallationTopologyProfile profile)
    {
        string installerStateRoot = string.Equals(
                Path.TrimEndingDirectorySeparator(paths.StateDirectory),
                "/var/lib/aethersdr",
                StringComparison.Ordinal)
            ? "/var/lib/aethersdr-installer"
            : Path.Combine(
                paths.StateDirectory,
                "installer");
        List<string> directories =
        [
            paths.ConfigurationDirectory,
            paths.StateDirectory,
            paths.SecretDirectory,
            paths.DataProtectionKeyDirectory,
            paths.ReleaseDirectory,
            paths.BackupDirectory,
            paths.LogDirectory,
            installerStateRoot,
            Path.Combine(installerStateRoot, "proxy"),
            Path.Combine(installerStateRoot, "firewall"),
            Path.Combine(installerStateRoot, "releases")
        ];
        if (profile.GatewayRunsHere)
        {
            directories.Add(paths.IdentityStoreDirectory);
        }
        if (profile.BrokerRunsHere ||
            profile.StationEngineRunsHere ||
            profile.AgentRunsHere)
        {
            string configurationRoot = Path.Combine(
                paths.ConfigurationDirectory,
                "aetherremote");
            string stateRoot = Path.Combine(
                paths.StateDirectory,
                "aetherremote");
            string logRoot = Path.Combine(
                paths.LogDirectory,
                "aetherremote");
            directories.Add(configurationRoot);
            directories.Add(stateRoot);
            directories.Add(logRoot);
            if (profile.BrokerRunsHere)
            {
                directories.Add(Path.Combine(configurationRoot, "broker"));
                directories.Add(Path.Combine(stateRoot, "broker"));
            }
            if (profile.StationEngineRunsHere)
            {
                directories.Add(
                    Path.Combine(configurationRoot, "station-engine"));
                string stationEngineState =
                    Path.Combine(stateRoot, "station-engine");
                directories.Add(stationEngineState);
                directories.Add(
                    Path.Combine(stationEngineState, "data-protection"));
            }
            if (profile.AgentRunsHere)
            {
                directories.Add(Path.Combine(configurationRoot, "agent"));
                directories.Add(Path.Combine(stateRoot, "agent"));
            }
        }
        return directories.ToArray();
    }

    private static string[] BuildServices(InstallationTopologyProfile profile)
    {
        List<string> services = [];
        if (profile.GatewayRunsHere)
        {
            services.Add("aethersdr-web.service");
            services.Add("aethersdr-release-updater.service");
        }
        if (profile.BrokerRunsHere)
        {
            services.Add("aetherremote-broker.service");
        }
        if (profile.StationEngineRunsHere)
        {
            services.Add("aetherremote-station-engine.service");
        }
        if (profile.AgentRunsHere)
        {
            services.Add("aetherremote-agent.service");
        }
        return services.ToArray();
    }

    private static InstallationInstallerPlanAction[] BuildActions(
        InstallationTopologyProfile profile,
        InstallationInstallerSelection selection,
        string canonicalPublicUrl,
        IReadOnlyList<string> users,
        IReadOnlyList<string> directories,
        IReadOnlyList<string> services)
    {
        List<InstallationInstallerPlanAction> actions = [];
        foreach (string user in users)
        {
            AddAction(
                actions,
                InstallationInstallerActionKind.EnsureServiceUser,
                user);
        }
        foreach (string directory in directories)
        {
            AddAction(
                actions,
                InstallationInstallerActionKind.EnsureDirectory,
                directory);
        }
        AddAction(
            actions,
            InstallationInstallerActionKind.InstallVerifiedRelease,
            $"{selection.ReleaseIdentity}/{ArchitectureMoniker(selection.Architecture)}");

        foreach (string service in services)
        {
            AddAction(
                actions,
                InstallationInstallerActionKind.InstallSystemdUnit,
                service);
        }

        if (profile.GatewayRunsHere)
        {
            AddAction(
                actions,
                InstallationInstallerActionKind.ConfigureReverseProxy,
                selection.ReverseProxyMode.ToString());
        }
        AddAction(
            actions,
            InstallationInstallerActionKind.WriteFirewallGuidance,
            $"{profile.Kind}/{selection.FirewallMode}");
        if (services.Count > 0)
        {
            AddAction(
                actions,
                InstallationInstallerActionKind.ReloadSystemd,
                "system");
            AddAction(
                actions,
                InstallationInstallerActionKind.ActivateInitialRelease,
                selection.ReleaseIdentity);
            foreach (string service in BuildServiceActivationOrder(services))
            {
                AddAction(
                    actions,
                    InstallationInstallerActionKind.ActivateSystemdUnit,
                    service);
            }
            if (profile.GatewayRunsHere &&
                selection.ReverseProxyMode is
                    InstallationReverseProxyMode.ManagedCaddy or
                    InstallationReverseProxyMode.LanInternalCertificate)
            {
                AddAction(
                    actions,
                    InstallationInstallerActionKind.ActivateSystemdUnit,
                    "caddy.service");
            }
            if (profile.GatewayRunsHere &&
                selection.ReverseProxyMode ==
                    InstallationReverseProxyMode.LanInternalCertificate)
            {
                AddAction(
                    actions,
                    InstallationInstallerActionKind.TrustInternalCertificate,
                    "/usr/local/share/ca-certificates/aethersdr-caddy-local.crt");
            }
        }
        if (profile.GatewayRunsHere)
        {
            AddAction(
                actions,
                InstallationInstallerActionKind.VerifyHealth,
                new Uri(
                    new Uri(canonicalPublicUrl, UriKind.Absolute),
                    "healthz").AbsoluteUri);
        }
        AddAction(
            actions,
            InstallationInstallerActionKind.VerifyTls,
            canonicalPublicUrl);

        return actions.ToArray();
    }

    private static IReadOnlyList<string> BuildServiceActivationOrder(
        IReadOnlyList<string> services)
    {
        string[] preferred =
        [
            "aetherremote-broker.service",
            "aetherremote-station-engine.service",
            "aetherremote-agent.service",
            "aethersdr-web.service",
            "aethersdr-release-updater.service"
        ];
        return preferred.Where(services.Contains).ToArray();
    }

    private static string ArchitectureMoniker(
        InstallationInstallerArchitecture architecture) =>
        architecture switch
        {
            InstallationInstallerArchitecture.LinuxX64 => "linux-x64",
            InstallationInstallerArchitecture.LinuxArm64 => "linux-arm64",
            _ => throw new InvalidOperationException(
                "The installer architecture is unsupported.")
        };

    private static void AddAction(
        List<InstallationInstallerPlanAction> actions,
        InstallationInstallerActionKind kind,
        string target) =>
        actions.Add(new(actions.Count + 1, kind, target));

    private static string ComputePlanId(
        InstallationSetupState state,
        InstallationTopologyKind topology,
        string canonicalPublicUrl,
        InstallationInstallerSelection selection,
        IReadOnlyList<string> users,
        IReadOnlyList<string> directories,
        IReadOnlyList<string> services,
        IReadOnlyList<InstallationInstallerPlanAction> actions)
    {
        StringBuilder canonical = new();
        AddValue(canonical, CurrentPlanSchemaVersion.ToString(CultureInfo.InvariantCulture));
        AddValue(canonical, state.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        AddValue(canonical, state.Revision.ToString(CultureInfo.InvariantCulture));
        AddValue(canonical, state.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        AddValue(canonical, state.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        AddValue(canonical, topology.ToString());
        AddValue(canonical, canonicalPublicUrl);
        AddValue(canonical, selection.Architecture.ToString());
        AddValue(canonical, selection.ReverseProxyMode.ToString());
        AddValue(canonical, selection.FirewallMode.ToString());
        AddValue(canonical, selection.ReleaseIdentity);
        AddValue(canonical, state.InstallTransmitSupport ? "1" : "0");
        foreach (string user in users)
        {
            AddValue(canonical, "user:" + user);
        }
        foreach (string directory in directories)
        {
            AddValue(canonical, "directory:" + directory);
        }
        foreach (string service in services)
        {
            AddValue(canonical, "service:" + service);
        }
        foreach (InstallationInstallerPlanAction action in actions)
        {
            AddValue(
                canonical,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{action.Order}:{action.Kind}:{action.Target}"));
        }

        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void AddValue(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('\n');
    }
}
