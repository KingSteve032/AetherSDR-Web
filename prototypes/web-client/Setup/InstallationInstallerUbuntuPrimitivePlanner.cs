namespace AetherSDR.Web.Setup;

public enum InstallationInstallerUbuntuPrimitiveKind
{
    EnsureSystemUser = 1,
    EnsureDirectory = 2,
    VerifyImmutableRelease = 3,
    InstallSystemdUnit = 4,
    ConfigureReverseProxy = 5,
    VerifyTls = 6,
    WriteFirewallGuidance = 7,
    ReloadSystemd = 8,
    ActivateInitialRelease = 9,
    ActivateSystemdUnit = 10,
    VerifyHealth = 11,
    TrustInternalCertificate = 12,
    InitializeIdentityDatabase = 13,
    ConfigureGatewayEnvironment = 14,
    InstallAuthenticationClientSecret = 15,
    AdoptSetupIdentityState = 16
}

public sealed record InstallationInstallerUbuntuPrimitiveOperation(
    int Order,
    InstallationInstallerUbuntuPrimitiveKind Kind,
    string Target,
    string Executable,
    IReadOnlyList<string> Arguments);

public static class InstallationInstallerUbuntuPrimitivePlanner
{
    private const string UserAddExecutable = "/usr/sbin/useradd";
    private const string InstallExecutable = "/usr/bin/install";
    private const string SystemctlExecutable = "/usr/bin/systemctl";
    private const string SystemdDirectory = "/etc/systemd/system";

    private static readonly IReadOnlySet<string> SupportedUsers =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "aethersdr",
            "aetherremote"
        };

    private static readonly IReadOnlySet<string> SupportedServices =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "aethersdr-web.service",
            "aethersdr-release-updater.service",
            "aetherremote-broker.service",
            "aetherremote-station-engine.service",
            "aetherremote-agent.service"
        };

    private static readonly IReadOnlySet<string> SupportedActivationServices =
        new HashSet<string>(SupportedServices, StringComparer.Ordinal)
        {
            "caddy.service"
        };

    private static readonly IReadOnlySet<string> SupportedDirectories =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "/etc/aethersdr",
            "/etc/aethersdr/aetherremote",
            "/etc/aethersdr/aetherremote/broker",
            "/etc/aethersdr/aetherremote/station-engine",
            "/etc/aethersdr/aetherremote/agent",
            "/var/lib/aethersdr",
            "/var/lib/aethersdr/secrets",
            "/var/lib/aethersdr/secrets/data-protection",
            "/var/lib/aethersdr/identity",
            "/var/lib/aethersdr-installer",
            "/var/lib/aethersdr-installer/proxy",
            "/var/lib/aethersdr-installer/firewall",
            "/var/lib/aethersdr-installer/releases",
            "/var/lib/aethersdr/aetherremote",
            "/var/lib/aethersdr/aetherremote/broker",
            "/var/lib/aethersdr/aetherremote/station-engine",
            "/var/lib/aethersdr/aetherremote/station-engine/data-protection",
            "/var/lib/aethersdr/aetherremote/agent",
            "/opt/aethersdr/releases",
            "/var/backups/aethersdr",
            "/var/log/aethersdr",
            "/var/log/aethersdr/aetherremote"
        };

    public static IReadOnlyList<InstallationInstallerUbuntuPrimitiveOperation>
        Compose(InstallationInstallerUbuntuMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<InstallationInstallerUbuntuPrimitiveOperation> operations = [];

        foreach (InstallationInstallerPlanAction action in request.Actions)
        {
            operations.Add(action.Kind switch
            {
                InstallationInstallerActionKind.EnsureServiceUser =>
                    EnsureUser(action),
                InstallationInstallerActionKind.EnsureDirectory =>
                    EnsureDirectory(
                        action,
                        DirectoryOwner(action.Target, request.Actions)),
                InstallationInstallerActionKind.AdoptSetupIdentityState =>
                    AdoptSetupIdentityState(action),
                InstallationInstallerActionKind.InstallVerifiedRelease =>
                    VerifyRelease(action, request),
                InstallationInstallerActionKind.InitializeIdentityDatabase =>
                    InitializeIdentityDatabase(action),
                InstallationInstallerActionKind.ConfigureGatewayEnvironment =>
                    GatewayConfiguration(
                        action,
                        request,
                        InstallationInstallerUbuntuPrimitiveKind
                            .ConfigureGatewayEnvironment),
                InstallationInstallerActionKind
                    .InstallAuthenticationClientSecret =>
                    GatewayConfiguration(
                        action,
                        request,
                        InstallationInstallerUbuntuPrimitiveKind
                            .InstallAuthenticationClientSecret),
                InstallationInstallerActionKind.InstallSystemdUnit =>
                    InstallUnit(action, request),
                InstallationInstallerActionKind.ConfigureReverseProxy =>
                    Typed(action, InstallationInstallerUbuntuPrimitiveKind
                        .ConfigureReverseProxy),
                InstallationInstallerActionKind.VerifyTls =>
                    Typed(action, InstallationInstallerUbuntuPrimitiveKind
                        .VerifyTls),
                InstallationInstallerActionKind.WriteFirewallGuidance =>
                    Typed(action, InstallationInstallerUbuntuPrimitiveKind
                        .WriteFirewallGuidance),
                InstallationInstallerActionKind.ReloadSystemd =>
                    Direct(
                        action,
                        InstallationInstallerUbuntuPrimitiveKind.ReloadSystemd,
                        SystemctlExecutable,
                        ["daemon-reload"]),
                InstallationInstallerActionKind.ActivateInitialRelease =>
                    Typed(
                        action,
                        InstallationInstallerUbuntuPrimitiveKind
                            .ActivateInitialRelease),
                InstallationInstallerActionKind.ActivateSystemdUnit =>
                    ActivateUnit(action),
                InstallationInstallerActionKind.VerifyHealth =>
                    Typed(action, InstallationInstallerUbuntuPrimitiveKind
                        .VerifyHealth),
                InstallationInstallerActionKind.TrustInternalCertificate =>
                    Typed(action, InstallationInstallerUbuntuPrimitiveKind
                        .TrustInternalCertificate),
                _ => throw new InvalidOperationException(
                    "The installer plan contains an unsupported Ubuntu action.")
            });
        }

        if (!operations.Select(operation => operation.Order)
            .SequenceEqual(Enumerable.Range(1, operations.Count)))
        {
            throw new InvalidOperationException(
                "Ubuntu primitive operations require one exact ordered inventory.");
        }
        return Array.AsReadOnly(operations.ToArray());
    }

    private static InstallationInstallerUbuntuPrimitiveOperation EnsureUser(
        InstallationInstallerPlanAction action)
    {
        if (!SupportedUsers.Contains(action.Target))
        {
            throw new InvalidOperationException(
                "The installer plan contains an unsupported service user.");
        }
        return Direct(
            action,
            InstallationInstallerUbuntuPrimitiveKind.EnsureSystemUser,
            UserAddExecutable,
            [
                "--system",
                "--user-group",
                "--home-dir",
                "/nonexistent",
                "--shell",
                "/usr/sbin/nologin",
                "--",
                action.Target
            ]);
    }

    private static InstallationInstallerUbuntuPrimitiveOperation EnsureDirectory(
        InstallationInstallerPlanAction action,
        string owner)
    {
        RequireAbsolutePath(action.Target, "directory");
        if (!SupportedDirectories.Contains(action.Target))
        {
            throw new InvalidOperationException(
                "The installer plan contains a noncanonical Ubuntu directory.");
        }
        bool secret = action.Target.EndsWith(
                "/secrets",
                StringComparison.Ordinal) ||
            action.Target.Contains(
                "/secrets/",
                StringComparison.Ordinal) ||
            action.Target.EndsWith(
                "/data-protection",
                StringComparison.Ordinal) ||
            action.Target.EndsWith(
                "/identity",
                StringComparison.Ordinal);
        bool sharedReleaseRoot = string.Equals(
            action.Target,
            "/opt/aethersdr/releases",
            StringComparison.Ordinal);
        bool multiServiceStateRoot = string.Equals(
            action.Target,
            "/var/lib/aethersdr",
            StringComparison.Ordinal);
        string octalMode = secret
            ? "0700"
            : sharedReleaseRoot
                ? "0755"
                : multiServiceStateRoot
                    ? "0751"
                    : "0750";
        return Direct(
            action,
            InstallationInstallerUbuntuPrimitiveKind.EnsureDirectory,
            InstallExecutable,
            [
                "-d",
                "-m",
                octalMode,
                "-o",
                owner,
                "-g",
                owner,
                "--",
                action.Target
            ]);
    }

    private static InstallationInstallerUbuntuPrimitiveOperation
        AdoptSetupIdentityState(InstallationInstallerPlanAction action)
    {
        const string stateDirectory = "/var/lib/aethersdr";
        if (!string.Equals(
                action.Target,
                stateDirectory,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The installer plan contains a noncanonical setup identity handoff.");
        }
        return Direct(
            action,
            InstallationInstallerUbuntuPrimitiveKind.AdoptSetupIdentityState,
            "/usr/bin/chown",
            [
                "--recursive",
                "--no-dereference",
                "aethersdr:aethersdr",
                "--",
                "/var/lib/aethersdr/identity",
                "/var/lib/aethersdr/secrets/data-protection",
                "/var/lib/aethersdr/setup"
            ]);
    }

    private static InstallationInstallerUbuntuPrimitiveOperation VerifyRelease(
        InstallationInstallerPlanAction action,
        InstallationInstallerUbuntuMutationRequest request)
    {
        RequireAbsolutePath(request.ImmutableStagingPath, "staging");
        RequireAbsolutePath(request.TargetReleasePath, "target release");
        return new(
            action.Order,
            InstallationInstallerUbuntuPrimitiveKind.VerifyImmutableRelease,
            request.TargetReleasePath,
            Executable: string.Empty,
            Arguments: []);
    }

    private static InstallationInstallerUbuntuPrimitiveOperation
        InitializeIdentityDatabase(InstallationInstallerPlanAction action)
    {
        const string identityDatabase =
            "/var/lib/aethersdr/identity/aethersdr-identity.db";
        if (!string.Equals(
                action.Target,
                identityDatabase,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The installer plan contains a noncanonical identity database.");
        }
        return Typed(
            action,
            InstallationInstallerUbuntuPrimitiveKind.InitializeIdentityDatabase);
    }

    private static InstallationInstallerUbuntuPrimitiveOperation
        GatewayConfiguration(
            InstallationInstallerPlanAction action,
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerUbuntuPrimitiveKind kind)
    {
        InstallationInstallerGatewayConfigurationPlan plan =
            InstallationInstallerGatewayConfigurationPlanComposer.Compose(
                request);
        string expectedTarget = kind ==
            InstallationInstallerUbuntuPrimitiveKind.ConfigureGatewayEnvironment
                ? plan.EnvironmentTargetPath
                : plan.RequiresClientSecret
                    ? plan.ClientSecretTargetPath
                    : throw new InvalidOperationException(
                        "The installer plan contains an unnecessary authentication secret action.");
        if (!string.Equals(
                action.Target,
                expectedTarget,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The installer plan contains a noncanonical gateway configuration target.");
        }
        return Typed(action, kind);
    }

    private static InstallationInstallerUbuntuPrimitiveOperation InstallUnit(
        InstallationInstallerPlanAction action,
        InstallationInstallerUbuntuMutationRequest request)
    {
        if (!SupportedServices.Contains(action.Target))
        {
            throw new InvalidOperationException(
                "The installer plan contains an unsupported systemd unit.");
        }

        string source = Path.Combine(
            request.InstallerAssetRoot,
            "installer",
            "systemd",
            action.Target);
        string target = Path.Combine(SystemdDirectory, action.Target);
        return Direct(
            action,
            InstallationInstallerUbuntuPrimitiveKind.InstallSystemdUnit,
            InstallExecutable,
            [
                "-m",
                "0644",
                "-o",
                "root",
                "-g",
                "root",
                "--",
                source,
                target
            ]);
    }

    private static InstallationInstallerUbuntuPrimitiveOperation ActivateUnit(
        InstallationInstallerPlanAction action)
    {
        if (!SupportedActivationServices.Contains(action.Target))
        {
            throw new InvalidOperationException(
                "The installer plan contains an unsupported activation unit.");
        }
        return Direct(
            action,
            InstallationInstallerUbuntuPrimitiveKind.ActivateSystemdUnit,
            SystemctlExecutable,
            ["enable", "--now", "--", action.Target]);
    }

    private static InstallationInstallerUbuntuPrimitiveOperation Typed(
        InstallationInstallerPlanAction action,
        InstallationInstallerUbuntuPrimitiveKind kind) =>
        new(
            action.Order,
            kind,
            action.Target,
            Executable: string.Empty,
            Arguments: []);

    private static InstallationInstallerUbuntuPrimitiveOperation Direct(
        InstallationInstallerPlanAction action,
        InstallationInstallerUbuntuPrimitiveKind kind,
        string executable,
        IReadOnlyList<string> arguments) =>
        new(
            action.Order,
            kind,
            action.Target,
            executable,
            Array.AsReadOnly(arguments.ToArray()));

    private static string DirectoryOwner(
        string directory,
        IReadOnlyList<InstallationInstallerPlanAction> actions)
    {
        if (string.Equals(
                directory,
                "/opt/aethersdr/releases",
                StringComparison.Ordinal) ||
            string.Equals(
                directory,
                "/var/lib/aethersdr-installer",
                StringComparison.Ordinal) ||
            directory.StartsWith(
                "/var/lib/aethersdr-installer/",
                StringComparison.Ordinal))
        {
            return "root";
        }

        HashSet<string> users = actions
            .Where(action =>
                action.Kind ==
                InstallationInstallerActionKind.EnsureServiceUser)
            .Select(action => action.Target)
            .ToHashSet(StringComparer.Ordinal);
        string marker =
            $"{Path.DirectorySeparatorChar}aetherremote";
        if ((directory.Contains(
                marker + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
             directory.EndsWith(marker, StringComparison.Ordinal)) &&
            users.Contains("aetherremote"))
        {
            return "aetherremote";
        }
        if (users.Contains("aethersdr"))
        {
            return "aethersdr";
        }
        if (users.SetEquals(["aetherremote"]))
        {
            return "aetherremote";
        }
        throw new InvalidOperationException(
            "The installer plan does not define a supported directory owner.");
    }

    private static void RequireAbsolutePath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"The Ubuntu {name} path is invalid.");
        }
    }
}
