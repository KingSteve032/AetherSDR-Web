using System.Security.Cryptography;
using System.Threading.RateLimiting;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Auth.Identity;

internal static class AetherLocalAuthenticationDefaults
{
    internal const string RateLimitPolicy = "local-authentication";

    internal static FixedWindowRateLimiterOptions CreateRateLimiterOptions(
        AetherLocalAuthenticationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new()
        {
            PermitLimit = policy.RateLimitPermitCount,
            Window = policy.RateLimitWindow,
            QueueLimit = 0,
            AutoReplenishment = true
        };
    }
}

internal static class AetherLocalAuthenticationComposition
{
    internal static IServiceCollection
        AddAetherFirstLocalAdministratorProvisioning(
            this IServiceCollection services,
            InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        AetherLocalAuthenticationPolicy policy =
            AetherAuthenticationConfiguration.CreateSetupLocalPolicy();
        services.AddAetherIdentityPersistence(paths);
        AddCredentialCore(services, policy);
        AddFirstAdministratorProvisioner(services);
        return services;
    }

    internal static IServiceCollection AddAetherLocalAuthenticationFoundation(
        this IServiceCollection services,
        AetherLocalAuthenticationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policy);

        AddCredentialCore(services, policy);
        services.AddSingleton(
            AetherLocalPasswordTimingDefense.Create(policy));
        services.AddSingleton<AetherLocalMfaChallengeStore>();
        services.AddScoped<AetherLocalPasswordAuthenticationService>();
        services.AddScoped<AetherLocalMfaAuthenticationService>();
        AddFirstAdministratorProvisioner(services);
        return services;
    }

    private static void AddCredentialCore(
        IServiceCollection services,
        AetherLocalAuthenticationPolicy policy)
    {
        services.AddSingleton(policy);
        services.Configure<PasswordHasherOptions>(options =>
        {
            options.CompatibilityMode =
                PasswordHasherCompatibilityMode.IdentityV3;
            options.IterationCount = policy.PasswordHashIterationCount;
        });
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddScoped<
            IPasswordHasher<AetherIdentityUser>,
            PasswordHasher<AetherIdentityUser>>();
        services.AddSingleton<AetherLocalMfaCredentialProtector>();
    }

    private static void AddFirstAdministratorProvisioner(
        IServiceCollection services)
    {
        services.AddSingleton<AetherFirstLocalAdministratorProvisioningLock>();
        services.AddScoped<AetherFirstLocalAdministratorProvisioningService>();
        services.AddScoped<IInstallationFirstLocalAdministratorProvisioner>(
            provider => provider.GetRequiredService<
                AetherFirstLocalAdministratorProvisioningService>());
        services.AddSingleton<
            IInstallationFirstLocalAdministratorProvisioningExecutor,
            AetherFirstLocalAdministratorProvisioningExecutor>();
    }
}

internal sealed class AetherLocalPasswordTimingDefense
{
    private AetherLocalPasswordTimingDefense(
        AetherIdentityUser user,
        string passwordHash)
    {
        User = user;
        PasswordHash = passwordHash;
    }

    internal AetherIdentityUser User { get; }

    internal string PasswordHash { get; }

    internal static AetherLocalPasswordTimingDefense Create(
        AetherLocalAuthenticationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        AetherIdentityUser user = new()
        {
            Id = Guid.NewGuid(),
            UserName = "timing-defense",
            NormalizedUserName = "TIMING-DEFENSE",
            DisplayName = "Timing Defense",
            Enabled = false,
            LockoutEnabled = false,
            AuthorityVersion = 1
        };
        PasswordHasherOptions options = new()
        {
            CompatibilityMode =
                PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = policy.PasswordHashIterationCount
        };
        PasswordHasher<AetherIdentityUser> hasher =
            new(Options.Create(options));
        byte[] secretBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            string secret = Convert.ToHexString(secretBytes);
            return new(user, hasher.HashPassword(user, secret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }
}
