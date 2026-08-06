using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Auth;

public static class DevelopmentAuthenticationDefaults
{
    public const string Scheme = "AetherDevelopment";
}

public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthSettings> authSettings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        DevelopmentUserSettings user = authSettings.Value.DevelopmentUser;
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.ObjectId),
            new("oid", user.ObjectId),
            new(ClaimTypes.Name, user.Name),
            new("name", user.Name),
            new(ClaimTypes.Email, user.Email),
            new("preferred_username", user.Email),
            new(
                "auth_time",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture))
        ];

        foreach (string role in user.Roles.Where(AetherRoles.All.Contains))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("roles", role));
        }

        ClaimsIdentity identity = new(
            claims,
            DevelopmentAuthenticationDefaults.Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket =
            new(principal, DevelopmentAuthenticationDefaults.Scheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
