using Microsoft.Extensions.DependencyInjection;

namespace AetherSDR.Web.Setup;

public sealed record InstallationSetupOnlyProgramCompositionReport(
    string CanonicalAccessUrl,
    string SetupStatePath,
    long SetupRevision,
    InstallationSetupLockMode LockMode,
    InstallationSetupStep LastCompletedStep);

public static class InstallationSetupOnlyProgramComposition
{
    public static InstallationSetupOnlyProgramCompositionReport Configure(
        WebApplicationBuilder builder,
        InstallationHostStartupPlan startupPlan,
        InstallationSetupHttpSecuritySettings securitySettings,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(startupPlan);
        ArgumentNullException.ThrowIfNull(securitySettings);

        if (startupPlan.Mode != InstallationHostStartupMode.SetupOnly ||
            !startupPlan.SetupOnlyEligible ||
            startupPlan.NormalRuntimeReady)
        {
            throw new InvalidOperationException(
                "Setup-only program composition requires an eligible setup-only startup plan.");
        }

        InstallationPaths paths = startupPlan.Paths ??
            throw new InvalidOperationException(
                "Setup-only program composition requires resolved installation paths.");
        InstallationPaths.Validate(paths);
        InstallationSetupStatusReport status = startupPlan.SetupStatus ??
            throw new InvalidOperationException(
                "Setup-only program composition requires redacted setup status.");
        string canonicalAccessUrl =
            startupPlan.SetupOnlyCanonicalAccessUrl ?? string.Empty;
        CanonicalPublicUrl parsedAccessUrl =
            CanonicalPublicUrl.Parse(canonicalAccessUrl);
        if (!string.Equals(
                parsedAccessUrl.Value,
                canonicalAccessUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Setup-only program composition requires an exact canonical access URL.");
        }
        if (status.SetupComplete ||
            status.LockMode == InstallationSetupLockMode.Complete ||
            status.LastCompletedStep == InstallationSetupStep.Administrator)
        {
            throw new InvalidOperationException(
                "Setup-only program composition is forbidden after setup completes.");
        }

        if (builder.Services.Any(IsNormalRuntimeService))
        {
            throw new InvalidOperationException(
                "Setup-only program composition must run before authentication, radio, " +
                "remote-station, watchdog, command, or TX services are registered.");
        }

        TimeProvider time = timeProvider ?? TimeProvider.System;
        InstallationSetupHttpSecurityPolicy security =
            new(canonicalAccessUrl, securitySettings);
        InstallationSetupOnlyProgramCompositionReport report = new(
            canonicalAccessUrl,
            paths.SetupStatePath,
            status.Revision,
            status.LockMode,
            status.LastCompletedStep);

        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(
            _ => new InstallationSetupStore(paths.SetupStatePath, time));
        builder.Services.AddSingleton(security);
        InstallationSetupOnlyHttpAdapter.ConfigureServices(
            builder.Services,
            security.Contract);
        builder.Services.AddSingleton(
            services => new InstallationSetupCenterApplication(
                services.GetRequiredService<InstallationSetupStore>(),
                services.GetRequiredService<InstallationSetupHttpSecurityPolicy>(),
                time));
        builder.Services.AddSingleton(report);

        return report;
    }

    private static bool IsNormalRuntimeService(ServiceDescriptor descriptor) =>
        IsNormalRuntimeType(descriptor.ServiceType) ||
        IsNormalRuntimeType(descriptor.ImplementationType);

    private static bool IsNormalRuntimeType(Type? type)
    {
        string value = type?.Namespace ?? string.Empty;
        return value.StartsWith("AetherSDR.Web.Auth", StringComparison.Ordinal) ||
            value.StartsWith("AetherSDR.Web.Radio", StringComparison.Ordinal) ||
            value.StartsWith("AetherRemote", StringComparison.Ordinal) ||
            value.StartsWith("AetherSDR.TxWatchdog", StringComparison.Ordinal);
    }
}
