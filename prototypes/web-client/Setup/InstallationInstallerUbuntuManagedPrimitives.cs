using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Auth.Identity;

namespace AetherSDR.Web.Setup;

internal interface IInstallationInstallerUbuntuManagedPrimitiveHandler
{
    bool Supports(InstallationInstallerUbuntuPrimitiveKind kind);

    Task<InstallationInstallerUbuntuPrimitiveInspection> InspectAsync(
        InstallationInstallerUbuntuMutationRequest request,
        InstallationInstallerUbuntuPrimitiveOperation operation,
        CancellationToken cancellationToken = default);

    Task<InstallationInstallerUbuntuStepResult> ExecuteAsync(
        InstallationInstallerUbuntuMutationRequest request,
        InstallationInstallerUbuntuPrimitiveOperation operation,
        CancellationToken cancellationToken = default);

    Task<InstallationInstallerUbuntuStepResult> RollbackInitialReleaseAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class LocalInstallationInstallerUbuntuManagedPrimitiveHandler :
    IInstallationInstallerUbuntuManagedPrimitiveHandler
{
    private const string AptGetExecutable = "/usr/bin/apt-get";
    private const string SystemctlExecutable = "/usr/bin/systemctl";
    private const string RunUserExecutable = "/usr/sbin/runuser";
    private const string IdentityServiceUser = "aethersdr";
    private const string IdentityDatabasePath =
        "/var/lib/aethersdr/identity/aethersdr-identity.db";
    private const string DefaultCurrentPointer = "/opt/aethersdr/current";
    private const string ManagedCaddyMarker =
        "/var/lib/aethersdr-installer/proxy/managed-caddy.sha256";
    private const string CaddyInternalCertificateSource =
        "/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt";
    private const string CaddyInternalCertificateTarget =
        "/usr/local/share/ca-certificates/aethersdr-caddy-local.crt";
    private const string CaddyInternalCertificateMarker =
        "/var/lib/aethersdr-installer/proxy/internal-ca.sha256";
    private const string UpdateCaCertificatesExecutable =
        "/usr/sbin/update-ca-certificates";
    private const int MaximumManagedFileBytes = 64 * 1024;
    private const int MaximumIdentityReportBytes = 8 * 1024;

    private static readonly JsonSerializerOptions IdentityReportJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 4
        };

    private static readonly IReadOnlySet<
        InstallationInstallerUbuntuPrimitiveKind> SupportedKinds =
        new HashSet<InstallationInstallerUbuntuPrimitiveKind>
        {
            InstallationInstallerUbuntuPrimitiveKind.VerifyImmutableRelease,
            InstallationInstallerUbuntuPrimitiveKind.ConfigureReverseProxy,
            InstallationInstallerUbuntuPrimitiveKind.VerifyTls,
            InstallationInstallerUbuntuPrimitiveKind.TrustInternalCertificate,
            InstallationInstallerUbuntuPrimitiveKind.WriteFirewallGuidance,
            InstallationInstallerUbuntuPrimitiveKind.ActivateInitialRelease,
            InstallationInstallerUbuntuPrimitiveKind.VerifyHealth,
            InstallationInstallerUbuntuPrimitiveKind.InitializeIdentityDatabase
        };

    private readonly InstallationInstallerUbuntuDirectProcessRunner m_runner;
    private readonly Func<HttpMessageHandler> m_httpHandlerFactory;
    private readonly InstallationInstallerInitialReleasePublisher m_release;
    private readonly string m_currentPointer;

    internal LocalInstallationInstallerUbuntuManagedPrimitiveHandler(
        InstallationInstallerUbuntuDirectProcessRunner runner)
        : this(runner, static () => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false
        })
    {
    }

    internal LocalInstallationInstallerUbuntuManagedPrimitiveHandler(
        InstallationInstallerUbuntuDirectProcessRunner runner,
        Func<HttpMessageHandler> httpHandlerFactory,
        string? currentPointer = null)
    {
        m_runner = runner ?? throw new ArgumentNullException(nameof(runner));
        m_httpHandlerFactory = httpHandlerFactory ??
            throw new ArgumentNullException(nameof(httpHandlerFactory));
        m_release = new InstallationInstallerInitialReleasePublisher();
        m_currentPointer = Path.GetFullPath(
            string.IsNullOrWhiteSpace(currentPointer)
                ? DefaultCurrentPointer
                : currentPointer);
    }

    public bool Supports(InstallationInstallerUbuntuPrimitiveKind kind) =>
        SupportedKinds.Contains(kind);

    public Task<InstallationInstallerUbuntuPrimitiveInspection> InspectAsync(
        InstallationInstallerUbuntuMutationRequest request,
        InstallationInstallerUbuntuPrimitiveOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            return Task.FromResult(Rejected(
                "ubuntu-platform-unsupported",
                "Managed Ubuntu primitives require Linux."));
        }
        return operation.Kind switch
        {
            InstallationInstallerUbuntuPrimitiveKind.VerifyImmutableRelease =>
                InspectReleaseAsync(request, cancellationToken),
            InstallationInstallerUbuntuPrimitiveKind.ConfigureReverseProxy =>
                InspectProxyAsync(request, cancellationToken),
            InstallationInstallerUbuntuPrimitiveKind.InitializeIdentityDatabase =>
                InspectIdentityDatabaseAsync(
                    request,
                    operation,
                    cancellationToken),
            InstallationInstallerUbuntuPrimitiveKind.WriteFirewallGuidance =>
                InspectFirewallAsync(request, cancellationToken),
            InstallationInstallerUbuntuPrimitiveKind.ActivateInitialRelease =>
                Task.FromResult(InspectInitialPointer(request)),
            InstallationInstallerUbuntuPrimitiveKind.VerifyHealth =>
                InspectHttpsAsync(operation.Target, requireHealth: true,
                    cancellationToken),
            InstallationInstallerUbuntuPrimitiveKind.VerifyTls =>
                InspectHttpsAsync(operation.Target, requireHealth: false,
                    cancellationToken),
            InstallationInstallerUbuntuPrimitiveKind.TrustInternalCertificate =>
                InspectInternalCertificateTrustAsync(
                    operation,
                    cancellationToken),
            _ => Task.FromResult(Rejected(
                "ubuntu-managed-primitive-unsupported",
                "The typed managed Ubuntu primitive is unsupported."))
        };
    }

    public async Task<InstallationInstallerUbuntuStepResult> ExecuteAsync(
        InstallationInstallerUbuntuMutationRequest request,
        InstallationInstallerUbuntuPrimitiveOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-platform-unsupported",
                "Managed Ubuntu primitives require Linux.");
        }

        InstallationInstallerUbuntuPrimitiveInspection before =
            await InspectAsync(request, operation, cancellationToken);
        if (before.Outcome ==
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged)
        {
            return InstallationInstallerUbuntuStepResult.Converged();
        }
        if (before.Outcome is
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected or
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown)
        {
            return before.Outcome ==
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected
                ? InstallationInstallerUbuntuStepResult.Rejected(
                    before.Code,
                    before.Summary)
                : InstallationInstallerUbuntuStepResult.Unknown(
                    before.Code,
                    before.Summary);
        }
        if (before.Outcome ==
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift &&
            !request.Repair)
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                before.Code,
                before.Summary);
        }

        try
        {
            return operation.Kind switch
            {
                InstallationInstallerUbuntuPrimitiveKind
                    .VerifyImmutableRelease =>
                    await PublishReleaseAsync(request, cancellationToken),
                InstallationInstallerUbuntuPrimitiveKind
                    .ConfigureReverseProxy =>
                    await ConfigureProxyAsync(request, cancellationToken),
                InstallationInstallerUbuntuPrimitiveKind
                    .InitializeIdentityDatabase =>
                    await InitializeIdentityDatabaseAsync(
                        request,
                        operation,
                        cancellationToken),
                InstallationInstallerUbuntuPrimitiveKind
                    .WriteFirewallGuidance =>
                    await ConfigureFirewallAsync(request, cancellationToken),
                InstallationInstallerUbuntuPrimitiveKind
                    .ActivateInitialRelease =>
                    ActivateInitialRelease(request),
                InstallationInstallerUbuntuPrimitiveKind
                    .TrustInternalCertificate =>
                    await TrustInternalCertificateAsync(
                        operation,
                        request,
                        cancellationToken),
                InstallationInstallerUbuntuPrimitiveKind.VerifyHealth or
                InstallationInstallerUbuntuPrimitiveKind.VerifyTls =>
                    await VerifyEndpointAsync(
                        operation,
                        cancellationToken),
                _ => InstallationInstallerUbuntuStepResult.Rejected(
                    "ubuntu-managed-primitive-unsupported",
                    "The typed managed Ubuntu primitive is unsupported.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-managed-mutation-unknown",
                "The managed Ubuntu primitive outcome requires reconciliation.");
        }
    }

    public Task<InstallationInstallerUbuntuStepResult>
        RollbackInitialReleaseAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            return Task.FromResult(
                InstallationInstallerUbuntuStepResult.Unknown(
                    "ubuntu-current-pointer-rollback-unknown",
                    "Initial release pointer rollback requires Linux."));
        }

        try
        {
            DirectoryInfo pointer = new(m_currentPointer);
            pointer.Refresh();
            if (!pointer.Exists &&
                pointer.LinkTarget is null &&
                !File.Exists(m_currentPointer))
            {
                return Task.FromResult(
                    InstallationInstallerUbuntuStepResult.Converged(
                        "ubuntu-current-pointer-rollback-converged",
                        "The transaction-owned initial current pointer is absent."));
            }
            if (pointer.LinkTarget is null)
            {
                return Task.FromResult(
                    InstallationInstallerUbuntuStepResult.Unknown(
                        "ubuntu-current-pointer-rollback-preserved",
                        "The current path is not the exact transaction-owned symlink and was preserved."));
            }

            string resolved = Path.GetFullPath(
                Path.Combine(
                    pointer.Parent?.FullName ??
                        Path.GetDirectoryName(m_currentPointer) ??
                        Path.DirectorySeparatorChar.ToString(),
                    pointer.LinkTarget));
            if (!PathEquals(resolved, request.TargetReleasePath))
            {
                return Task.FromResult(
                    InstallationInstallerUbuntuStepResult.Unknown(
                        "ubuntu-current-pointer-rollback-preserved",
                        "The current pointer does not name the exact transaction release and was preserved."));
            }

            File.Delete(m_currentPointer);
            DirectoryInfo after = new(m_currentPointer);
            after.Refresh();
            return Task.FromResult(
                !after.Exists &&
                after.LinkTarget is null &&
                !File.Exists(m_currentPointer)
                    ? InstallationInstallerUbuntuStepResult.Applied(
                        "ubuntu-current-pointer-rolled-back",
                        "The exact transaction-owned initial current pointer was removed.")
                    : InstallationInstallerUbuntuStepResult.Unknown(
                        "ubuntu-current-pointer-rollback-unknown",
                        "The initial current pointer rollback did not reach an exact absent postcondition."));
        }
        catch
        {
            return Task.FromResult(
                InstallationInstallerUbuntuStepResult.Unknown(
                    "ubuntu-current-pointer-rollback-unknown",
                    "The initial current pointer rollback outcome requires reconciliation."));
        }
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectReleaseAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken)
    {
        InstallationInstallerInitialReleaseResult result =
            await m_release.InspectAsync(request, cancellationToken);
        return result.Outcome switch
        {
            InstallationInstallerInitialReleaseOutcome.Converged => Converged(),
            InstallationInstallerInitialReleaseOutcome.Missing => Missing(),
            InstallationInstallerInitialReleaseOutcome.Rejected =>
                Rejected(result.Code, result.Summary),
            InstallationInstallerInitialReleaseOutcome.Unknown =>
                Unknown(result.Code, result.Summary),
            _ => throw new InvalidOperationException(
                "The initial release inspection outcome is unsupported.")
        };
    }

    private async Task<InstallationInstallerUbuntuStepResult>
        PublishReleaseAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-platform-unsupported",
                "Initial release publication requires Linux.");
        }
        InstallationInstallerInitialReleaseResult result =
            await m_release.PublishAsync(request, cancellationToken);
        return result.Outcome switch
        {
            InstallationInstallerInitialReleaseOutcome.Converged =>
                InstallationInstallerUbuntuStepResult.Applied(
                    "ubuntu-release-published",
                    "The verified immutable release is published exactly."),
            InstallationInstallerInitialReleaseOutcome.Missing or
            InstallationInstallerInitialReleaseOutcome.Rejected =>
                InstallationInstallerUbuntuStepResult.Rejected(
                    result.Code,
                    result.Summary),
            InstallationInstallerInitialReleaseOutcome.Unknown =>
                InstallationInstallerUbuntuStepResult.Unknown(
                    result.Code,
                    result.Summary),
            _ => throw new InvalidOperationException(
                "The initial release publication outcome is unsupported.")
        };
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectProxyAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken)
    {
        InstallationInstallerProxyPlan plan;
        try
        {
            plan = InstallationInstallerProxyPlanComposer.Compose(request);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or PlatformNotSupportedException)
        {
            return Rejected(
                "ubuntu-proxy-plan-rejected",
                "The reviewed reverse-proxy plan could not be composed safely.");
        }

        if (plan.RequiresCaddyPackage &&
            !SafeExecutable(
                InstallationInstallerProxyPlanComposer.CaddyExecutable) &&
            (!SafeExecutable(AptGetExecutable) ||
             !SafeExecutable(SystemctlExecutable)))
        {
            return Rejected(
                "ubuntu-caddy-install-unavailable",
                "Managed Caddy requires a safe installed binary or the fixed package installer and systemd mask.");
        }

        return await InspectManagedFileAsync(
            plan.TargetPath,
            plan.RenderedContent,
            plan.Ownership == InstallationInstallerProxyOwnership.InstallerManaged
                ? ManagedCaddyMarker
                : null,
            cancellationToken);
    }

    private async Task<InstallationInstallerUbuntuStepResult>
        ConfigureProxyAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken)
    {
        InstallationInstallerProxyPlan plan =
            InstallationInstallerProxyPlanComposer.Compose(request);
        string? marker =
            plan.Ownership == InstallationInstallerProxyOwnership.InstallerManaged
                ? ManagedCaddyMarker
                : null;
        bool installedCaddy = false;
        if (plan.RequiresCaddyPackage &&
            !SafeExecutable(InstallationInstallerProxyPlanComposer.CaddyExecutable))
        {
            InstallationInstallerUbuntuDirectProcessResult mask =
                await RunAsync(
                    SystemctlExecutable,
                    ["mask", "--runtime", "--", "caddy.service"],
                    cancellationToken);
            if (mask.ExitCode != 0)
            {
                return InstallationInstallerUbuntuStepResult.Unknown(
                    "ubuntu-caddy-mask-unknown",
                    "Caddy package installation did not establish its fail-closed runtime mask.");
            }
            InstallationInstallerUbuntuDirectProcessResult install =
                await RunAsync(
                    AptGetExecutable,
                    ["install", "--yes", "--no-install-recommends", "--", "caddy"],
                    cancellationToken);
            if (install.ExitCode != 0 ||
                !SafeExecutable(
                    InstallationInstallerProxyPlanComposer.CaddyExecutable))
            {
                return InstallationInstallerUbuntuStepResult.Unknown(
                    "ubuntu-caddy-install-unknown",
                    "The fixed Caddy package installation did not establish a safe executable.");
            }
            installedCaddy = true;
            if (marker is not null)
            {
                FileInfo packageConfiguration = new(plan.TargetPath);
                packageConfiguration.Refresh();
                if (!SafeRegularFile(packageConfiguration) ||
                    packageConfiguration.Length > MaximumManagedFileBytes)
                {
                    return InstallationInstallerUbuntuStepResult.Unknown(
                        "ubuntu-caddy-package-config-unknown",
                        "The installed Caddy package configuration is not a safe bounded file.");
                }
                string packageContent = await File.ReadAllTextAsync(
                    plan.TargetPath,
                    Encoding.UTF8,
                    cancellationToken);
                await WriteMarkerAsync(
                    marker,
                    Sha256(packageContent),
                    request.PlanId,
                    cancellationToken);
            }
        }

        string staged = await StageManagedFileAsync(
            plan.TargetPath,
            plan.RenderedContent,
            marker,
            request.Repair,
            cancellationToken,
            allowUnmanagedReplace: installedCaddy);
        bool published = false;
        try
        {
            if (plan.ValidationExecutable.Length > 0)
            {
                IReadOnlyList<string> arguments =
                    ReplaceValidationTarget(
                        plan.ValidationArguments,
                        plan.TargetPath,
                        staged);
                InstallationInstallerUbuntuDirectProcessResult validation =
                    await RunAsync(
                        plan.ValidationExecutable,
                        arguments,
                        cancellationToken);
                if (validation.ExitCode != 0)
                {
                    File.Delete(staged);
                    return InstallationInstallerUbuntuStepResult.Rejected(
                        "ubuntu-caddy-config-invalid",
                        "Caddy rejected the rendered reviewed configuration before publication.");
                }
            }

            PublishManagedFile(staged, plan.TargetPath);
            published = true;
            if (marker is not null)
            {
                await WriteMarkerAsync(
                    marker,
                    Sha256(plan.RenderedContent),
                    request.PlanId,
                    cancellationToken);
            }
            if (installedCaddy)
            {
                InstallationInstallerUbuntuDirectProcessResult unmask =
                    await RunAsync(
                        SystemctlExecutable,
                        ["unmask", "--runtime", "--", "caddy.service"],
                        cancellationToken);
                if (unmask.ExitCode != 0)
                {
                    return InstallationInstallerUbuntuStepResult.Unknown(
                        "ubuntu-caddy-unmask-unknown",
                        "The reviewed Caddy configuration is installed but its runtime mask requires reconciliation.");
                }
            }
        }
        finally
        {
            if (!published && File.Exists(staged))
            {
                File.Delete(staged);
            }
        }

        InstallationInstallerUbuntuPrimitiveInspection after =
            await InspectProxyAsync(request, cancellationToken);
        return after.Outcome ==
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged
            ? InstallationInstallerUbuntuStepResult.Applied(
                "ubuntu-proxy-configured",
                "The reviewed reverse-proxy configuration is installed.")
            : InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-proxy-postcondition-unknown",
                "The reverse-proxy configuration could not be reconciled after publication.");
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectFirewallAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken)
    {
        InstallationInstallerFirewallPlan plan;
        try
        {
            plan = InstallationInstallerFirewallPlanComposer.Compose(request);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or PlatformNotSupportedException)
        {
            return Rejected(
                "ubuntu-firewall-plan-rejected",
                "The reviewed firewall plan could not be composed safely.");
        }

        InstallationInstallerUbuntuPrimitiveInspection guidance =
            await InspectManagedFileAsync(
                plan.GuidanceTargetPath,
                plan.GuidanceContent,
                markerPath: null,
                cancellationToken);
        if (plan.Rules.Count > 0 &&
            !SafeExecutable(
                InstallationInstallerFirewallPlanComposer.UfwExecutable))
        {
            return Rejected(
                "ubuntu-ufw-unavailable",
                "Applied firewall mode requires one safe fixed UFW executable.");
        }
        if (plan.Rules.Count > 0)
        {
            InstallationInstallerUbuntuDirectProcessResult active =
                await RunAsync(
                    InstallationInstallerFirewallPlanComposer.UfwExecutable,
                    ["status"],
                    cancellationToken);
            if (active.ExitCode != 0)
            {
                return Unknown(
                    "ubuntu-ufw-status-unknown",
                    "The existing UFW activation state could not be inspected.");
            }
            if (!UfwActive(active.StandardOutput))
            {
                return Rejected(
                    "ubuntu-ufw-inactive",
                    "Applied firewall mode requires operator-enabled UFW; the installer never enables it implicitly.");
            }
        }
        if (guidance.Outcome !=
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged)
        {
            return guidance;
        }
        if (plan.Rules.Count == 0)
        {
            return Converged();
        }

        InstallationInstallerUbuntuDirectProcessResult status =
            await RunAsync(
                InstallationInstallerFirewallPlanComposer.UfwExecutable,
                ["show", "added"],
                cancellationToken);
        if (status.ExitCode != 0)
        {
            return Unknown(
                "ubuntu-ufw-inspection-unknown",
                "The existing UFW rule inventory could not be inspected.");
        }
        return plan.Rules.All(rule =>
                HasUfwRule(status.StandardOutput, rule.Port))
            ? Converged()
            : Missing();
    }

    private async Task<InstallationInstallerUbuntuStepResult>
        ConfigureFirewallAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken)
    {
        InstallationInstallerFirewallPlan plan =
            InstallationInstallerFirewallPlanComposer.Compose(request);
        string staged = await StageManagedFileAsync(
            plan.GuidanceTargetPath,
            plan.GuidanceContent,
            markerPath: null,
            request.Repair,
            cancellationToken);
        PublishManagedFile(staged, plan.GuidanceTargetPath);

        if (plan.Rules.Count > 0)
        {
            InstallationInstallerUbuntuDirectProcessResult status =
                await RunAsync(
                    InstallationInstallerFirewallPlanComposer.UfwExecutable,
                    ["show", "added"],
                    cancellationToken);
            if (status.ExitCode != 0)
            {
                return InstallationInstallerUbuntuStepResult.Unknown(
                    "ubuntu-ufw-inspection-unknown",
                    "The UFW rule inventory became unavailable after guidance publication.");
            }

            foreach (InstallationInstallerFirewallRule rule in plan.Rules)
            {
                if (HasUfwRule(status.StandardOutput, rule.Port))
                {
                    continue;
                }
                InstallationInstallerUbuntuDirectProcessResult result =
                    await RunAsync(
                        rule.Executable,
                        rule.Arguments,
                        cancellationToken);
                if (result.ExitCode != 0)
                {
                    return InstallationInstallerUbuntuStepResult.Unknown(
                        "ubuntu-ufw-rule-unknown",
                        "A reviewed additive UFW rule outcome requires reconciliation.");
                }
            }
        }

        InstallationInstallerUbuntuPrimitiveInspection after =
            await InspectFirewallAsync(request, cancellationToken);
        return after.Outcome ==
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged
            ? InstallationInstallerUbuntuStepResult.Applied(
                "ubuntu-firewall-configured",
                "Firewall guidance and selected additive rules are converged.")
            : InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-firewall-postcondition-unknown",
                "The firewall plan could not be reconciled after execution.");
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectIdentityDatabaseAsync(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken)
    {
        if (!IdentityOperationIsExact(operation) ||
            !SafeExecutable(RunUserExecutable))
        {
            return Rejected(
                "ubuntu-identity-command-unsafe",
                "The fixed identity database command boundary is unavailable or unsafe.");
        }

        string releaseDirectory;
        string gatewayDirectory;
        string gatewayExecutable;
        try
        {
            releaseDirectory = Path.GetFullPath(request.TargetReleasePath);
            gatewayDirectory = Path.Combine(releaseDirectory, "gateway-web");
            gatewayExecutable = Path.Combine(
                gatewayDirectory,
                "AetherSDR.Web");
        }
        catch
        {
            return Rejected(
                "ubuntu-identity-command-unsafe",
                "The fixed identity database command boundary is unavailable or unsafe.");
        }

        DirectoryInfo release = new(releaseDirectory);
        release.Refresh();
        if (!release.Exists && release.LinkTarget is null)
        {
            return Missing();
        }
        if (!SafeImmutableDirectory(releaseDirectory))
        {
            return Rejected(
                "ubuntu-identity-release-unsafe",
                "The identity database command requires the exact immutable release directory.");
        }

        DirectoryInfo gateway = new(gatewayDirectory);
        gateway.Refresh();
        if (!gateway.Exists && gateway.LinkTarget is null)
        {
            return Missing();
        }
        if (!SafeImmutableDirectory(gatewayDirectory))
        {
            return Rejected(
                "ubuntu-identity-release-unsafe",
                "The identity database command requires the exact immutable gateway directory.");
        }

        FileInfo executable = new(gatewayExecutable);
        executable.Refresh();
        if (!executable.Exists && executable.LinkTarget is null)
        {
            return Missing();
        }
        if (!SafeExecutable(gatewayExecutable))
        {
            return Rejected(
                "ubuntu-identity-executable-unsafe",
                "The immutable gateway identity command executable is unavailable or unsafe.");
        }

        InstallationInstallerUbuntuDirectProcessResult validation;
        try
        {
            validation = await RunIdentityDatabaseCommandAsync(
                gatewayExecutable,
                gatewayDirectory,
                [AetherIdentityDatabaseCommandParser.ValidateSwitch],
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Unknown(
                "ubuntu-identity-inspection-unknown",
                "The identity database schema could not be inspected.");
        }

        if (validation.ExitCode != 0 ||
            !TryParseIdentityReport(validation, out AetherIdentityDatabaseReport report))
        {
            return validation.ExitCode == 2
                ? Rejected(
                    "ubuntu-identity-validation-rejected",
                    "The identity database schema validation was rejected.")
                : Unknown(
                    "ubuntu-identity-inspection-unknown",
                    "The identity database schema could not be classified.");
        }
        if (IdentityReportIsConverged(report))
        {
            return Converged();
        }
        if (IdentityReportIsMissing(report))
        {
            return Missing();
        }
        return Rejected(
            "ubuntu-identity-validation-rejected",
            "The identity database schema report did not match an allowed exact state.");
    }

    private async Task<InstallationInstallerUbuntuStepResult>
        InitializeIdentityDatabaseAsync(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken)
    {
        string gatewayDirectory = Path.Combine(
            Path.GetFullPath(request.TargetReleasePath),
            "gateway-web");
        string gatewayExecutable = Path.Combine(
            gatewayDirectory,
            "AetherSDR.Web");

        InstallationInstallerUbuntuDirectProcessResult planned =
            await RunIdentityDatabaseCommandAsync(
                gatewayExecutable,
                gatewayDirectory,
                [AetherIdentityDatabaseCommandParser.PlanSwitch],
                cancellationToken);
        if (planned.ExitCode != 0 ||
            !TryParseIdentityReport(planned, out AetherIdentityDatabaseReport plan))
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-identity-plan-rejected",
                "The fixed identity database initialization plan was not accepted.");
        }
        if (IdentityReportIsConverged(plan))
        {
            return InstallationInstallerUbuntuStepResult.Converged();
        }
        if (!IdentityReportIsPlanned(plan) ||
            !string.Equals(
                plan.DatabasePath,
                operation.Target,
                StringComparison.Ordinal))
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-identity-plan-rejected",
                "The identity database initialization plan did not match the reviewed target.");
        }

        InstallationInstallerUbuntuDirectProcessResult applied =
            await RunIdentityDatabaseCommandAsync(
                gatewayExecutable,
                gatewayDirectory,
                [
                    AetherIdentityDatabaseCommandParser.ApplySwitch,
                    AetherIdentityDatabaseCommandParser.ConfirmPlanSwitch,
                    plan.PlanId
                ],
                cancellationToken);
        if (applied.ExitCode != 0 ||
            !TryParseIdentityReport(applied, out AetherIdentityDatabaseReport result) ||
            (!IdentityReportIsApplied(result) &&
             !IdentityReportIsConverged(result)))
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-identity-apply-unknown",
                "Identity database initialization may require reconciliation.");
        }

        InstallationInstallerUbuntuDirectProcessResult validation =
            await RunIdentityDatabaseCommandAsync(
                gatewayExecutable,
                gatewayDirectory,
                [AetherIdentityDatabaseCommandParser.ValidateSwitch],
                cancellationToken);
        if (validation.ExitCode != 0 ||
            !TryParseIdentityReport(
                validation,
                out AetherIdentityDatabaseReport postcondition) ||
            !IdentityReportIsConverged(postcondition))
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-identity-postcondition-unknown",
                "The identity database schema postcondition could not be confirmed.");
        }

        return IdentityReportIsApplied(result)
            ? InstallationInstallerUbuntuStepResult.Applied(
                "ubuntu-identity-schema-initialized",
                "The production identity database schema is initialized under the fixed service identity.")
            : InstallationInstallerUbuntuStepResult.Converged();
    }

    private async Task<InstallationInstallerUbuntuDirectProcessResult>
        RunIdentityDatabaseCommandAsync(
            string gatewayExecutable,
            string gatewayDirectory,
            IReadOnlyList<string> commandArguments,
            CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "--user",
            IdentityServiceUser,
            "--",
            gatewayExecutable
        ];
        arguments.AddRange(commandArguments);
        return await RunAsync(
            RunUserExecutable,
            arguments,
            cancellationToken,
            gatewayDirectory);
    }

    private static bool IdentityOperationIsExact(
        InstallationInstallerUbuntuPrimitiveOperation operation) =>
        operation.Kind ==
            InstallationInstallerUbuntuPrimitiveKind.InitializeIdentityDatabase &&
        string.Equals(
            operation.Target,
            IdentityDatabasePath,
            StringComparison.Ordinal) &&
        string.IsNullOrEmpty(operation.Executable) &&
        operation.Arguments.Count == 0;

    private static bool TryParseIdentityReport(
        InstallationInstallerUbuntuDirectProcessResult result,
        out AetherIdentityDatabaseReport report)
    {
        report = null!;
        if (!string.IsNullOrEmpty(result.StandardError) ||
            string.IsNullOrWhiteSpace(result.StandardOutput) ||
            Encoding.UTF8.GetByteCount(result.StandardOutput) >
                MaximumIdentityReportBytes)
        {
            return false;
        }
        try
        {
            AetherIdentityDatabaseReport? parsed =
                JsonSerializer.Deserialize<AetherIdentityDatabaseReport>(
                    result.StandardOutput,
                    IdentityReportJsonOptions);
            if (parsed is null)
            {
                return false;
            }
            report = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IdentityReportIsPlanned(
        AetherIdentityDatabaseReport report) =>
        IdentityReportHasExactEnvelope(report) &&
        string.Equals(report.Outcome, "planned", StringComparison.Ordinal) &&
        string.Equals(
            report.Code,
            "identity-schema-initialization-required",
            StringComparison.Ordinal) &&
        report.ExistingSchemaVersion is null &&
        report.MutationRequired;

    private static bool IdentityReportIsMissing(
        AetherIdentityDatabaseReport report) =>
        IdentityReportHasExactEnvelope(report) &&
        string.Equals(report.Outcome, "incomplete", StringComparison.Ordinal) &&
        string.Equals(
            report.Code,
            "identity-schema-not-initialized",
            StringComparison.Ordinal) &&
        report.ExistingSchemaVersion is null &&
        report.MutationRequired;

    private static bool IdentityReportIsConverged(
        AetherIdentityDatabaseReport report) =>
        IdentityReportHasExactEnvelope(report) &&
        string.Equals(report.Outcome, "converged", StringComparison.Ordinal) &&
        string.Equals(
            report.Code,
            "identity-schema-converged",
            StringComparison.Ordinal) &&
        report.ExistingSchemaVersion ==
            AetherIdentityDbContext.CurrentSchemaVersion &&
        !report.MutationRequired;

    private static bool IdentityReportIsApplied(
        AetherIdentityDatabaseReport report) =>
        IdentityReportHasCommonValues(report) &&
        string.Equals(report.Outcome, "applied", StringComparison.Ordinal) &&
        string.Equals(
            report.Code,
            "identity-schema-initialized",
            StringComparison.Ordinal) &&
        report.ExistingSchemaVersion ==
            AetherIdentityDbContext.CurrentSchemaVersion &&
        !report.MutationRequired &&
        report.MutationAttempted &&
        report.DatabaseCreated &&
        !report.BackupRequired &&
        !report.RollbackAttempted &&
        !report.RollbackSucceeded;

    private static bool IdentityReportHasExactEnvelope(
        AetherIdentityDatabaseReport report) =>
        IdentityReportHasCommonValues(report) &&
        !report.MutationAttempted &&
        !report.DatabaseCreated &&
        !report.BackupRequired &&
        !report.RollbackAttempted &&
        !report.RollbackSucceeded;

    private static bool IdentityReportHasCommonValues(
        AetherIdentityDatabaseReport report) =>
        report.TargetSchemaVersion ==
            AetherIdentityDbContext.CurrentSchemaVersion &&
        string.Equals(
            report.DatabasePath,
            IdentityDatabasePath,
            StringComparison.Ordinal) &&
        IsLowercaseSha256(report.PlanId);

    private static bool IsLowercaseSha256(string? value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(character =>
            char.IsAsciiHexDigit(character) &&
            !char.IsAsciiLetterUpper(character));

    private InstallationInstallerUbuntuPrimitiveInspection
        InspectInitialPointer(InstallationInstallerUbuntuMutationRequest request)
    {
        if (!SafeDirectory(request.TargetReleasePath))
        {
            return Missing();
        }

        try
        {
            DirectoryInfo pointer = new(m_currentPointer);
            pointer.Refresh();
            if (!pointer.Exists && pointer.LinkTarget is null)
            {
                return Missing();
            }
            if (pointer.LinkTarget is null)
            {
                return Rejected(
                    "ubuntu-current-pointer-unsafe",
                    "The initial current path is not a symbolic link.");
            }
            string resolved = Path.GetFullPath(
                Path.Combine(
                    pointer.Parent?.FullName ?? "/opt/aethersdr",
                    pointer.LinkTarget));
            return PathEquals(resolved, request.TargetReleasePath)
                ? Converged()
                : Rejected(
                    "ubuntu-current-pointer-preserved",
                    "An existing current release pointer is never replaced by initial installation.");
        }
        catch
        {
            return Unknown(
                "ubuntu-current-pointer-inspection-unknown",
                "The initial current release pointer could not be inspected.");
        }
    }

    private InstallationInstallerUbuntuStepResult ActivateInitialRelease(
        InstallationInstallerUbuntuMutationRequest request)
    {
        if (!SafeDirectory(request.TargetReleasePath))
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-release-not-published",
                "The verified immutable release is not published.");
        }
        if (Directory.Exists(m_currentPointer) ||
            File.Exists(m_currentPointer) ||
            new DirectoryInfo(m_currentPointer).LinkTarget is not null)
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-current-pointer-preserved",
                "Initial installation never replaces an existing current release pointer.");
        }

        Directory.CreateSymbolicLink(
            m_currentPointer,
            request.TargetReleasePath);
        InstallationInstallerUbuntuPrimitiveInspection after =
            InspectInitialPointer(request);
        return after.Outcome ==
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged
            ? InstallationInstallerUbuntuStepResult.Applied(
                "ubuntu-initial-release-activated",
                "The absent current pointer now names the verified immutable release.")
            : InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-current-pointer-postcondition-unknown",
                "The initial release pointer outcome requires reconciliation.");
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectInternalCertificateTrustAsync(
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken)
    {
        if (!string.Equals(
                operation.Target,
                CaddyInternalCertificateTarget,
                StringComparison.Ordinal))
        {
            return Rejected(
                "ubuntu-internal-ca-target-rejected",
                "The LAN internal certificate target is not the fixed reviewed Ubuntu trust path.");
        }
        if (!File.Exists(CaddyInternalCertificateSource))
        {
            return Missing();
        }

        string certificate;
        try
        {
            certificate = await ReadInternalCertificateAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Rejected(
                "ubuntu-internal-ca-source-rejected",
                "The Caddy internal CA source is missing, unsafe, or not a valid CA certificate.");
        }

        InstallationInstallerUbuntuPrimitiveInspection installed =
            await InspectManagedFileAsync(
                CaddyInternalCertificateTarget,
                certificate,
                CaddyInternalCertificateMarker,
                cancellationToken);
        if (installed.Outcome !=
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged)
        {
            return installed;
        }

        try
        {
            using X509Certificate2 root =
                X509Certificate2.CreateFromPem(certificate);
            using X509Chain chain = new();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            return chain.Build(root) ? Converged() : Missing();
        }
        catch
        {
            return Unknown(
                "ubuntu-internal-ca-trust-inspection-unknown",
                "The normal Ubuntu certificate trust result could not be inspected.");
        }
    }

    private async Task<InstallationInstallerUbuntuStepResult>
        TrustInternalCertificateAsync(
            InstallationInstallerUbuntuPrimitiveOperation operation,
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken)
    {
        if (!string.Equals(
                operation.Target,
                CaddyInternalCertificateTarget,
                StringComparison.Ordinal) ||
            !File.Exists(CaddyInternalCertificateSource))
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-internal-ca-unavailable",
                "Caddy did not establish the fixed LAN internal CA source after activation.");
        }

        string certificate;
        try
        {
            certificate = await ReadInternalCertificateAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-internal-ca-source-rejected",
                "The Caddy internal CA source is unsafe or is not a valid CA certificate.");
        }

        InstallationInstallerUbuntuPrimitiveInspection installed =
            await InspectManagedFileAsync(
                CaddyInternalCertificateTarget,
                certificate,
                CaddyInternalCertificateMarker,
                cancellationToken);
        if (installed.Outcome !=
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged)
        {
            if (installed.Outcome is
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected or
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown)
            {
                return installed.Outcome ==
                    InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected
                    ? InstallationInstallerUbuntuStepResult.Rejected(
                        installed.Code,
                        installed.Summary)
                    : InstallationInstallerUbuntuStepResult.Unknown(
                        installed.Code,
                        installed.Summary);
            }

            string staged = await StageManagedFileAsync(
                CaddyInternalCertificateTarget,
                certificate,
                CaddyInternalCertificateMarker,
                request.Repair,
                cancellationToken);
            PublishManagedFile(staged, CaddyInternalCertificateTarget);
            await WriteMarkerAsync(
                CaddyInternalCertificateMarker,
                Sha256(certificate),
                request.PlanId,
                cancellationToken);
        }

        InstallationInstallerUbuntuDirectProcessResult update =
            await RunAsync(
                UpdateCaCertificatesExecutable,
                [],
                cancellationToken);
        if (update.ExitCode != 0)
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-internal-ca-update-unknown",
                "The fixed Ubuntu trust-store update returned nonzero after execution began.");
        }

        InstallationInstallerUbuntuPrimitiveInspection after =
            await InspectInternalCertificateTrustAsync(
                operation,
                cancellationToken);
        return after.Outcome ==
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged
            ? InstallationInstallerUbuntuStepResult.Applied(
                "ubuntu-internal-ca-trusted",
                "The exact Caddy LAN internal CA is installed in normal Ubuntu certificate trust.")
            : InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-internal-ca-postcondition-unknown",
                "The Ubuntu certificate trust postcondition could not be proven.");
    }

    private static async Task<string> ReadInternalCertificateAsync(
        CancellationToken cancellationToken)
    {
        FileInfo source = new(CaddyInternalCertificateSource);
        source.Refresh();
        if (!SafeRegularFile(source) ||
            source.Length is < 256 or > MaximumManagedFileBytes)
        {
            throw new InvalidOperationException(
                "The fixed Caddy internal CA source is unsafe.");
        }

        string certificate = await File.ReadAllTextAsync(
            CaddyInternalCertificateSource,
            Encoding.UTF8,
            cancellationToken);
        using X509Certificate2 root =
            X509Certificate2.CreateFromPem(certificate);
        X509BasicConstraintsExtension? constraints = root.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .SingleOrDefault();
        if (constraints is null ||
            !constraints.CertificateAuthority ||
            !root.SubjectName.RawData.AsSpan().SequenceEqual(
                root.IssuerName.RawData))
        {
            throw new InvalidOperationException(
                "The fixed Caddy internal CA source is not a self-issued CA certificate.");
        }
        return certificate;
    }

    private async Task<InstallationInstallerUbuntuStepResult>
        VerifyEndpointAsync(
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        do
        {
            InstallationInstallerUbuntuPrimitiveInspection inspection =
                await InspectHttpsAsync(
                    operation.Target,
                    operation.Kind ==
                        InstallationInstallerUbuntuPrimitiveKind.VerifyHealth,
                    cancellationToken);
            if (inspection.Outcome ==
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged)
            {
                return InstallationInstallerUbuntuStepResult.Converged(
                    operation.Kind ==
                        InstallationInstallerUbuntuPrimitiveKind.VerifyHealth
                        ? "ubuntu-health-verified"
                        : "ubuntu-tls-verified",
                    operation.Kind ==
                        InstallationInstallerUbuntuPrimitiveKind.VerifyHealth
                        ? "The exact public health endpoint is ready."
                        : "The exact public HTTPS origin completed trusted TLS verification.");
            }
            if (inspection.Outcome is
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected or
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown)
            {
                return inspection.Outcome ==
                    InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected
                    ? InstallationInstallerUbuntuStepResult.Rejected(
                        inspection.Code,
                        inspection.Summary)
                    : InstallationInstallerUbuntuStepResult.Unknown(
                        inspection.Code,
                        inspection.Summary);
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        while (true);

        return InstallationInstallerUbuntuStepResult.Rejected(
            operation.Kind ==
                InstallationInstallerUbuntuPrimitiveKind.VerifyHealth
                ? "ubuntu-health-not-ready"
                : "ubuntu-tls-not-ready",
            operation.Kind ==
                InstallationInstallerUbuntuPrimitiveKind.VerifyHealth
                ? "The exact public health endpoint did not become ready within its bound."
                : "The exact public HTTPS origin did not complete trusted TLS verification within its bound.");
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectHttpsAsync(
            string target,
            bool requireHealth,
            CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return Rejected(
                requireHealth
                    ? "ubuntu-health-target-invalid"
                    : "ubuntu-tls-target-invalid",
                "The exact verification target is not a canonical HTTPS URL.");
        }

        using HttpMessageHandler handler = m_httpHandlerFactory();
        using HttpClient client = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AetherSDR-Installer/1");
            using HttpResponseMessage response =
                await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            if (requireHealth)
            {
                return response.IsSuccessStatusCode
                    ? Converged()
                    : Missing();
            }
            return (int)response.StatusCode < 500
                ? Converged()
                : Missing();
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return Missing();
        }
        catch (HttpRequestException)
        {
            return Missing();
        }
    }

    private static async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectManagedFileAsync(
            string target,
            string expectedContent,
            string? markerPath,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            FileInfo file = new(target);
            file.Refresh();
            if (!file.Exists)
            {
                return Missing();
            }
            if (!SafeRegularFile(file))
            {
                return Rejected(
                    "ubuntu-managed-file-unsafe",
                    "A managed installer target is not a safe regular file.");
            }
            if (file.Length > MaximumManagedFileBytes)
            {
                return Rejected(
                    "ubuntu-managed-file-oversized",
                    "A managed installer target exceeds its fixed size bound.");
            }
            string actual = await File.ReadAllTextAsync(
                target,
                Encoding.UTF8,
                cancellationToken);
            if (string.Equals(actual, expectedContent, StringComparison.Ordinal))
            {
                return Converged();
            }
            if (markerPath is not null &&
                !MarkerOwns(markerPath, actual))
            {
                return Rejected(
                    "ubuntu-operator-policy-preserved",
                    "The existing proxy configuration is not proven installer-owned and will not be overwritten.");
            }
            return Drift(
                "ubuntu-managed-file-drift",
                "An installer-managed file differs from the reviewed plan.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Unknown(
                "ubuntu-managed-file-inspection-unknown",
                "A managed installer target could not be inspected.");
        }
    }

    private static async Task<string> StageManagedFileAsync(
        string target,
        string content,
        string? markerPath,
        bool repair,
        CancellationToken cancellationToken,
        bool allowUnmanagedReplace = false)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Managed installer files require Linux.");
        }
        DirectoryInfo parent = new(
            Path.GetDirectoryName(target) ??
            throw new InvalidOperationException("Managed target has no parent."));
        parent.Refresh();
        if (!parent.Exists || parent.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                "The managed target parent is missing or unsafe.");
        }

        FileInfo existing = new(target);
        existing.Refresh();
        if (existing.Exists)
        {
            if (!SafeRegularFile(existing))
            {
                throw new InvalidOperationException(
                    "The managed target is unsafe.");
            }
            string actual = await File.ReadAllTextAsync(
                target,
                Encoding.UTF8,
                cancellationToken);
            if (!allowUnmanagedReplace &&
                (!repair ||
                 (markerPath is not null && !MarkerOwns(markerPath, actual))))
            {
                throw new InvalidOperationException(
                    "Existing operator policy is preserved.");
            }
        }

        string staged = Path.Combine(
            parent.FullName,
            $".{Path.GetFileName(target)}.{Sha256(content)[..16]}.tmp");
        if (File.Exists(staged) || Directory.Exists(staged))
        {
            throw new InvalidOperationException(
                "A prior managed-file staging path requires reconciliation.");
        }

        await using FileStream stream = new(
            staged,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Options = FileOptions.Asynchronous |
                    FileOptions.WriteThrough,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead |
                    UnixFileMode.UserWrite
            });
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        File.SetUnixFileMode(
            staged,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead);
        return staged;
    }

    private static void PublishManagedFile(string staged, string target)
    {
        FileInfo existing = new(target);
        existing.Refresh();
        if (existing.Exists && !SafeRegularFile(existing))
        {
            throw new InvalidOperationException(
                "The managed target changed before atomic publication.");
        }
        File.Move(staged, target, overwrite: true);
    }

    private static async Task WriteMarkerAsync(
        string markerPath,
        string contentHash,
        string planId,
        CancellationToken cancellationToken)
    {
        string markerContent =
            $"sha256={contentHash}\nplan={planId}\n";
        string staged = await StageManagedFileAsync(
            markerPath,
            markerContent,
            markerPath: null,
            repair: true,
            cancellationToken,
            allowUnmanagedReplace: false);
        PublishManagedFile(staged, markerPath);
    }

    private static bool MarkerOwns(string markerPath, string actualContent)
    {
        try
        {
            FileInfo marker = new(markerPath);
            marker.Refresh();
            if (!SafeRegularFile(marker) ||
                marker.Length is < 64 or > 256)
            {
                return false;
            }
            string first = File.ReadLines(markerPath).FirstOrDefault() ??
                string.Empty;
            return string.Equals(
                first,
                "sha256=" + Sha256(actualContent),
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task<InstallationInstallerUbuntuDirectProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        if (!SafeExecutable(executable))
        {
            throw new InvalidOperationException(
                "A fixed managed executable is unavailable or unsafe.");
        }

        ProcessStartInfo start = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        if (workingDirectory is not null)
        {
            if (!SafeImmutableDirectory(workingDirectory))
            {
                throw new InvalidOperationException(
                    "A fixed managed working directory is unavailable or unsafe.");
            }
            start.WorkingDirectory = workingDirectory;
        }
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return await m_runner(start, cancellationToken);
    }

    private static IReadOnlyList<string> ReplaceValidationTarget(
        IReadOnlyList<string> arguments,
        string target,
        string staged)
    {
        string[] result = arguments.ToArray();
        int index = Array.FindIndex(
            result,
            value => string.Equals(value, target, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException(
                "The fixed proxy validation target is missing.");
        }
        result[index] = staged;
        return Array.AsReadOnly(result);
    }

    private static bool UfwActive(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line =>
                string.Equals(
                    line,
                    "Status: active",
                    StringComparison.OrdinalIgnoreCase));

    private static bool HasUfwRule(string output, string port) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Any(line =>
                line.StartsWith("ufw allow ", StringComparison.Ordinal) &&
                line.Contains(port + "/tcp", StringComparison.Ordinal));

    private static bool SafeExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }
        try
        {
            FileInfo file = new(path);
            file.Refresh();
            if (!SafeRegularFile(file))
            {
                return false;
            }
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode &
                (UnixFileMode.UserExecute |
                 UnixFileMode.GroupExecute |
                 UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeImmutableDirectory(string path)
    {
        if (!OperatingSystem.IsLinux() || !SafeDirectory(path))
        {
            return false;
        }
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode &
                (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeDirectory(string path)
    {
        try
        {
            DirectoryInfo directory = new(path);
            directory.Refresh();
            return directory.Exists &&
                directory.LinkTarget is null &&
                (directory.Attributes &
                    FileAttributes.ReparsePoint) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeRegularFile(FileInfo file)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }
        file.Refresh();
        if (!file.Exists ||
            file.LinkTarget is not null ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            return false;
        }
        UnixFileMode mode = File.GetUnixFileMode(file.FullName);
        return (mode &
            (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
    }

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);

    private static InstallationInstallerUbuntuPrimitiveInspection Converged() =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged,
            "ubuntu-primitive-converged",
            "The managed Ubuntu primitive is converged.");

    private static InstallationInstallerUbuntuPrimitiveInspection Missing() =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing,
            "ubuntu-primitive-missing",
            "The managed Ubuntu primitive is not yet converged.");

    private static InstallationInstallerUbuntuPrimitiveInspection Drift(
        string code,
        string summary) =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift,
            code,
            summary);

    private static InstallationInstallerUbuntuPrimitiveInspection Rejected(
        string code,
        string summary) =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected,
            code,
            summary);

    private static InstallationInstallerUbuntuPrimitiveInspection Unknown(
        string code,
        string summary) =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown,
            code,
            summary);
}
