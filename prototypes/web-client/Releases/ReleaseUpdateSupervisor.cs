using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Releases;

internal static class ReleaseUpdateSupervisorProtocol
{
    internal const int Version = 1;
    internal const int MaximumMessageBytes = 64 * 1024;
    internal const string Prepare = "prepare";
    internal const string Activate = "activate";
    internal const string Rollback = "rollback";
    internal const string Status = "status";

    internal static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };
}

internal sealed record ReleaseUpdateSupervisorRequest(
    int Version,
    string Operation,
    ReleaseUpdateInstallRequest? Install,
    string TransactionId,
    string SubjectBinding,
    bool Authenticated,
    bool AdministratorAuthorized,
    DateTimeOffset? AuthenticatedAt,
    DateTimeOffset? ReauthenticatedAt);

internal sealed record ReleaseUpdateSupervisorResponse(
    int Version,
    ReleaseUpdateTransactionReport Report);

/// <summary>
/// Dedicated local updater process. It listens only on an owner-private Unix
/// socket below installation state and executes one transaction at a time. The
/// gateway and CLI are clients, so stopping or restarting the gateway cannot
/// destroy the coordinator's exact in-memory authority tokens. No TCP listener,
/// HTTP endpoint, browser credential, arbitrary command, radio command, or TX
/// operation is accepted by this boundary.
/// </summary>
public sealed class ReleaseUpdateSupervisor
{
    internal const string DirectoryName = "release-update-supervisor";
    internal const string SocketFileName = "control.sock";
    private readonly ReleaseUpdateTransactionCoordinator m_coordinator;
    private readonly string m_root;
    private readonly string m_socketPath;

    public ReleaseUpdateSupervisor(
        ReleaseUpdateTransactionCoordinator coordinator,
        InstallationPaths paths)
    {
        m_coordinator = coordinator ??
            throw new ArgumentNullException(nameof(coordinator));
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);
        m_root = Path.GetFullPath(
            Path.Combine(paths.StateDirectory, DirectoryName));
        m_socketPath = Path.GetFullPath(
            Path.Combine(m_root, SocketFileName));
        if (!string.Equals(
                Path.GetDirectoryName(m_root),
                Path.GetFullPath(paths.StateDirectory),
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetDirectoryName(m_socketPath),
                m_root,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The release updater socket escaped installation state.");
        }
    }

    internal string SocketPath => m_socketPath;

    [SupportedOSPlatform("linux")]
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The release update supervisor requires Linux Unix sockets.");
        }
        Directory.CreateDirectory(m_root);
        File.SetUnixFileMode(
            m_root,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        RemoveStaleSocket();

        using Socket listener = new(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(m_socketPath));
        File.SetUnixFileMode(
            m_socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(backlog: 8);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket connection = await listener.AcceptAsync(cancellationToken);
                _ = HandleAndDisposeAsync(connection, cancellationToken);
            }
        }
        finally
        {
            try
            {
                listener.Close();
            }
            catch
            {
            }
            RemoveStaleSocket();
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task HandleAndDisposeAsync(
        Socket connection,
        CancellationToken cancellationToken)
    {
        using (connection)
        {
            try
            {
                await using NetworkStream stream = new(connection, ownsSocket: false);
                ReleaseUpdateSupervisorRequest request =
                    await ReadAsync<ReleaseUpdateSupervisorRequest>(
                        stream,
                        cancellationToken);
                ReleaseUpdateTransactionReport report =
                    await ExecuteAsync(request, cancellationToken);
                await WriteAsync(
                    stream,
                    new ReleaseUpdateSupervisorResponse(
                        ReleaseUpdateSupervisorProtocol.Version,
                        report),
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is JsonException or IOException or
                    InvalidDataException or InvalidOperationException or
                    ArgumentException or NotSupportedException)
            {
                try
                {
                    await using NetworkStream stream =
                        new(connection, ownsSocket: false);
                    await WriteAsync(
                        stream,
                        new ReleaseUpdateSupervisorResponse(
                            ReleaseUpdateSupervisorProtocol.Version,
                            ReleaseUpdateTransactionReport.Create(
                                null,
                                succeeded: false,
                                ReleaseUpdateTransactionFailureCode.InvalidRequest,
                                "The local release updater request was rejected.")),
                        CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private Task<ReleaseUpdateTransactionReport> ExecuteAsync(
        ReleaseUpdateSupervisorRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Version != ReleaseUpdateSupervisorProtocol.Version ||
            request.Operation.Length is 0 or > 32)
        {
            throw new InvalidDataException(
                "The release updater protocol version or operation is invalid.");
        }

        return request.Operation switch
        {
            ReleaseUpdateSupervisorProtocol.Status =>
                Task.FromResult(m_coordinator.Status()),
            ReleaseUpdateSupervisorProtocol.Prepare
                when request.Install is not null &&
                     string.IsNullOrEmpty(request.TransactionId) &&
                     string.IsNullOrEmpty(request.SubjectBinding) =>
                m_coordinator.PrepareOfflineAsync(
                    request.Install,
                    cancellationToken),
            ReleaseUpdateSupervisorProtocol.Activate
                when request.Install is null =>
                m_coordinator.ApproveAndActivateAsync(
                    CanonicalTransactionId(request.TransactionId),
                    CreateEvidence(request),
                    cancellationToken),
            ReleaseUpdateSupervisorProtocol.Rollback
                when request.Install is null =>
                m_coordinator.ApproveAndRollbackAsync(
                    CanonicalTransactionId(request.TransactionId),
                    CreateEvidence(request),
                    cancellationToken),
            _ => throw new InvalidDataException(
                "The release updater request shape is invalid.")
        };
    }

    private static VerifiedReleaseActivationOperatorAuthenticationEvidence
        CreateEvidence(ReleaseUpdateSupervisorRequest request)
    {
        if (!request.Authenticated ||
            !request.AdministratorAuthorized ||
            request.AuthenticatedAt is null ||
            request.ReauthenticatedAt is null ||
            request.SubjectBinding.Length is < 32 or > 128 ||
            request.SubjectBinding.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "The local release updater approval evidence is invalid.");
        }
        return new VerifiedReleaseActivationOperatorAuthenticationEvidence(
            request.SubjectBinding,
            request.Authenticated,
            request.AdministratorAuthorized,
            request.AuthenticatedAt.Value,
            request.ReauthenticatedAt.Value);
    }

    private static string CanonicalTransactionId(string value)
    {
        if (value.Length != 32 ||
            value.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "The local release transaction identity is invalid.");
        }
        return value;
    }

    private void RemoveStaleSocket()
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(m_socketPath);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or
                DirectoryNotFoundException)
        {
            return;
        }

        FileInfo info = new(m_socketPath);
        info.Refresh();
        if ((attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            info.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                "The release updater socket path is unsafe.");
        }
        File.Delete(m_socketPath);
    }

    internal static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length is < 2 or > ReleaseUpdateSupervisorProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "The release updater message length is invalid.");
        }
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        T? value = JsonSerializer.Deserialize<T>(
            payload,
            ReleaseUpdateSupervisorProtocol.JsonOptions);
        return value ?? throw new InvalidDataException(
            "The release updater message is empty.");
    }

    internal static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            ReleaseUpdateSupervisorProtocol.JsonOptions);
        if (payload.Length > ReleaseUpdateSupervisorProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "The release updater response is too large.");
        }
        byte[] lengthBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, payload.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

/// <summary>
/// Local client for the dedicated release updater. It can connect only to the
/// derived owner-private Unix socket and exposes no network address or arbitrary
/// command field.
/// </summary>
public sealed class ReleaseUpdateSupervisorClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(15);
    private readonly string m_socketPath;

    public ReleaseUpdateSupervisorClient(InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);
        string root = Path.GetFullPath(
            Path.Combine(
                paths.StateDirectory,
                ReleaseUpdateSupervisor.DirectoryName));
        m_socketPath = Path.GetFullPath(
            Path.Combine(root, ReleaseUpdateSupervisor.SocketFileName));
        if (!string.Equals(
                Path.GetDirectoryName(root),
                Path.GetFullPath(paths.StateDirectory),
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetDirectoryName(m_socketPath),
                root,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The release updater client socket escaped installation state.");
        }
    }

    public Task<ReleaseUpdateTransactionReport> StatusAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new ReleaseUpdateSupervisorRequest(
                ReleaseUpdateSupervisorProtocol.Version,
                ReleaseUpdateSupervisorProtocol.Status,
                Install: null,
                TransactionId: string.Empty,
                SubjectBinding: string.Empty,
                Authenticated: false,
                AdministratorAuthorized: false,
                AuthenticatedAt: null,
                ReauthenticatedAt: null),
            cancellationToken);

    public Task<ReleaseUpdateTransactionReport> PrepareOfflineAsync(
        ReleaseUpdateInstallRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new ReleaseUpdateSupervisorRequest(
                ReleaseUpdateSupervisorProtocol.Version,
                ReleaseUpdateSupervisorProtocol.Prepare,
                request,
                TransactionId: string.Empty,
                SubjectBinding: string.Empty,
                Authenticated: false,
                AdministratorAuthorized: false,
                AuthenticatedAt: null,
                ReauthenticatedAt: null),
            cancellationToken);

    internal Task<ReleaseUpdateTransactionReport> ApproveAndActivateAsync(
        string transactionId,
        VerifiedReleaseActivationOperatorAuthenticationEvidence evidence,
        CancellationToken cancellationToken = default) =>
        SendApprovedAsync(
            ReleaseUpdateSupervisorProtocol.Activate,
            transactionId,
            evidence,
            cancellationToken);

    internal Task<ReleaseUpdateTransactionReport> ApproveAndRollbackAsync(
        string transactionId,
        VerifiedReleaseActivationOperatorAuthenticationEvidence evidence,
        CancellationToken cancellationToken = default) =>
        SendApprovedAsync(
            ReleaseUpdateSupervisorProtocol.Rollback,
            transactionId,
            evidence,
            cancellationToken);

    private Task<ReleaseUpdateTransactionReport> SendApprovedAsync(
        string operation,
        string transactionId,
        VerifiedReleaseActivationOperatorAuthenticationEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return SendAsync(
            new ReleaseUpdateSupervisorRequest(
                ReleaseUpdateSupervisorProtocol.Version,
                operation,
                Install: null,
                transactionId,
                evidence.SubjectBinding,
                evidence.Authenticated,
                evidence.AdministratorAuthorized,
                evidence.AuthenticatedAt,
                evidence.ReauthenticatedAt),
            cancellationToken);
    }

    private async Task<ReleaseUpdateTransactionReport> SendAsync(
        ReleaseUpdateSupervisorRequest request,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return ReleaseUpdateTransactionReport.Create(
                null,
                succeeded: false,
                ReleaseUpdateTransactionFailureCode.UnsupportedPlatform,
                "The dedicated release updater requires Linux.");
        }
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using Socket socket = new(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(m_socketPath),
                timeout.Token);
            await using NetworkStream stream = new(socket, ownsSocket: false);
            await ReleaseUpdateSupervisor.WriteAsync(
                stream,
                request,
                timeout.Token);
            ReleaseUpdateSupervisorResponse response =
                await ReleaseUpdateSupervisor.ReadAsync<
                    ReleaseUpdateSupervisorResponse>(stream, timeout.Token);
            if (response.Version != ReleaseUpdateSupervisorProtocol.Version)
            {
                throw new InvalidDataException(
                    "The release updater response version is unsupported.");
            }
            return response.Report;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return ReleaseUpdateTransactionReport.Create(
                null,
                succeeded: false,
                ReleaseUpdateTransactionFailureCode.ReconciliationRequired,
                "The dedicated release updater did not return a bounded result; local reconciliation is required.",
                reconciliationRequired: true);
        }
        catch (Exception exception)
            when (exception is SocketException or IOException or
                InvalidDataException or JsonException or
                InvalidOperationException or NotSupportedException)
        {
            return ReleaseUpdateTransactionReport.Create(
                null,
                succeeded: false,
                ReleaseUpdateTransactionFailureCode.ExecutionDisabled,
                "The dedicated release updater is unavailable.");
        }
    }
}
