using System.Net;

namespace AetherRemote.Broker;

public static class BrokerNetworkBoundary
{
    private static readonly PathString ApiPrefix = new("/api");
    private static readonly PathString ReceiveProjectionPath =
        new("/receive/v1");

    public static bool RequiresLoopback(PathString path) =>
        path.StartsWithSegments(ApiPrefix) ||
        path.StartsWithSegments(ReceiveProjectionPath);

    public static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return address.IsIPv4MappedToIPv6 &&
               IPAddress.IsLoopback(address.MapToIPv4());
    }
}
