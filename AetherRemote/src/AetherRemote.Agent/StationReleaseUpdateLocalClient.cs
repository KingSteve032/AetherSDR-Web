using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using AetherRemote.Protocol;

namespace AetherRemote.Agent;

public interface IStationReleaseUpdateLocalClient
{
    Task<LocalStationReleaseUpdateResult> ExecuteAsync(
        LocalStationReleaseUpdateRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Talks only to the root-owned fixed-purpose station updater over one local
/// Unix socket. The caller can supply only correlation ID, exact release
/// identity, and apply/rollback/confirm action; no path, executable, service,
/// shell, URL, or arbitrary payload crosses this privilege boundary.
/// </summary>
public sealed class UnixSocketStationReleaseUpdateLocalClient :
    IStationReleaseUpdateLocalClient
{
    internal const string SocketPath =
        "/run/aetherremote-release-updater/release.sock";
    internal const int MaximumMessageBytes = 16 * 1024;

    public async Task<LocalStationReleaseUpdateResult> ExecuteAsync(
        LocalStationReleaseUpdateRequest request,
        CancellationToken cancellationToken)
    {
        string? error =
            StationProtocolValidator.ValidateLocalReleaseUpdateRequest(request);
        if (error is not null)
        {
            throw new InvalidDataException(error);
        }
        using Socket socket = new(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        await socket.ConnectAsync(
            new UnixDomainSocketEndPoint(SocketPath),
            cancellationToken);
        await using NetworkStream stream = new(socket, ownsSocket: false);
        await WriteAsync(stream, request, cancellationToken);
        LocalStationReleaseUpdateResult result =
            await ReadAsync<LocalStationReleaseUpdateResult>(
                stream,
                cancellationToken);
        bool confirm = string.Equals(
            request.Action,
            StationLocalUpdaterActions.Confirm,
            StringComparison.Ordinal);
        if (StationProtocolValidator.ValidateLocalReleaseUpdateResult(result)
                is not null ||
            (!confirm &&
             !string.Equals(
                 result.CorrelationId,
                 request.CorrelationId,
                 StringComparison.Ordinal)) ||
            !string.Equals(
                result.ReleaseIdentity,
                request.ReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                result.Action,
                request.Action,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The local station updater returned a mismatched result.");
        }
        return result;
    }

    private static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            StationProtocol.JsonOptions);
        if (payload.Length is < 2 or > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "The local updater request length is invalid.");
        }
        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
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
                "The local updater response length is invalid.");
        }
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, StationProtocol.JsonOptions) ??
            throw new InvalidDataException(
                "The local updater response is empty.");
    }
}
