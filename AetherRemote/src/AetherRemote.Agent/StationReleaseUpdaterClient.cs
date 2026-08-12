using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using AetherRemote.Protocol;

namespace AetherRemote.Agent;

public interface IStationReleaseUpdaterClient
{
    Task<StationReleaseServiceControlResultMessage> ExecuteAsync(
        BrokerReleaseServiceControlMessage request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owner-private Unix-socket client for the separate AetherRemote updater
/// daemon. It transports only the already validated fixed release-control
/// message and accepts one exact correlated result.
/// </summary>
public sealed class UnixSocketStationReleaseUpdaterClient :
    IStationReleaseUpdaterClient
{
    internal const string SocketPath =
        "/run/aetherremote-release-updater/control.sock";
    internal const int MaximumMessageBytes = 16 * 1024;

    public async Task<StationReleaseServiceControlResultMessage> ExecuteAsync(
        BrokerReleaseServiceControlMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using Socket socket = new(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        await socket.ConnectAsync(
            new UnixDomainSocketEndPoint(SocketPath),
            cancellationToken);
        await using NetworkStream stream = new(socket, ownsSocket: false);
        await WriteAsync(stream, request, cancellationToken);
        return await ReadAsync<StationReleaseServiceControlResultMessage>(
            stream,
            cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length is < 2 or > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "The updater response length is invalid.");
        }
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        T? result = JsonSerializer.Deserialize<T>(
            payload,
            StationProtocol.JsonOptions);
        return result ?? throw new InvalidDataException(
            "The updater response is empty.");
    }

    private static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            StationProtocol.JsonOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "The updater request is too large.");
        }
        byte[] lengthBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, payload.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
