using System.Security.Cryptography;
using System.Threading.RateLimiting;
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
    internal static IServiceCollection AddAetherLocalAuthenticationFoundation(
        this IServiceCollection services,
        AetherLocalAuthenticationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policy);

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
        services.AddSingleton(
            AetherLocalPasswordTimingDefense.Create(policy));
        services.AddScoped<AetherLocalPasswordAuthenticationService>();
        return services;
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
