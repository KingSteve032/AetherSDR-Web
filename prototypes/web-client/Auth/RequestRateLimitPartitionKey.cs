using System.Net;
using System.Security.Claims;

namespace AetherSDR.Web.Auth;

/// <summary>
/// Stable rate-limit partition keys. Authenticated browser traffic is isolated
/// by durable subject identity; anonymous enrollment traffic is isolated by
/// the trusted post-forwarding client address.
/// </summary>
public static class RequestRateLimitPartitionKey
{
    public static string ForAuthenticatedUserOrAddress(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? subject =
            context.User.FindFirstValue("oid") ??
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            context.User.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(subject)
            ? ForAddress(context)
            : $"user:{subject.Trim()}";
    }

    public static string ForAddress(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "ip:unknown";
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        return $"ip:{address}";
    }
}
