using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using AetherRemote.Protocol;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ReleaseServiceControlUpdater>();
await builder.Build().RunAsync();

internal sealed class ReleaseServiceControlUpdater(
    ILogger<ReleaseServiceControlUpdater> logger) : BackgroundService
{
    internal const string DirectoryName = "aetherremote-release-updater";
    internal const string SocketFileName = "control.sock";
    internal const int MaximumMessageBytes = 16 * 1024;
    private const string SystemctlPath = "/usr/bin/systemctl";
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The AetherRemote release updater requires Linux.");
        }
        string runtimeRoot = GetRuntimeRoot();
        string socketRoot = Path.GetFullPath(Path.Combine(runtimeRoot, DirectoryName));
        string socketPath = Path.GetFullPath(
            Path.Combine(socketRoot, SocketFileName));
        if (!string.Equals(
                Path.GetDirectoryName(socketRoot),
                runtimeRoot,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetDirectoryName(socketPath),
                socketRoot,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The AetherRemote updater socket escaped the user runtime directory.");
        }

        Directory.CreateDirectory(socketRoot);
        File.SetUnixFileMode(
            socketRoot,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        RemoveStaleSocket(socketPath);

        using Socket listener = new(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.SetUnixFileMode(
            socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(backlog: 4);
        logger.LogInformation(
            "AetherRemote fixed release updater is listening on an owner-private Unix socket");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket connection = await listener.AcceptAsync(stoppingToken);
                await HandleAsync(connection, stoppingToken);
            }
        }
        finally
        {
            listener.Close();
            RemoveStaleSocket(socketPath);
        }
    }

    private async Task HandleAsync(
        Socket connection,
        CancellationToken cancellationToken)
    {
        using (connection)
        await using (NetworkStream stream = new(connection, ownsSocket: false))
        {
            StationReleaseServiceControlResultMessage response;
            try
            {
                BrokerReleaseServiceControlMessage request =
                    await ReadAsync<BrokerReleaseServiceControlMessage>(
                        stream,
                        cancellationToken);
                string? error =
                    StationProtocolValidator.ValidateReleaseServiceControl(request);
                if (error is not null)
                {
                    throw new InvalidDataException(error);
                }
                response = await ExecuteFixedActionAsync(
                    request,
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is JsonException or IOException or
                    InvalidDataException or InvalidOperationException or
                    NotSupportedException or ArgumentException)
            {
                logger.LogWarning(
                    exception,
                    "Rejected an invalid local release service-control request");
                return;
            }
            await WriteAsync(stream, response, cancellationToken);
        }
    }

    private static async Task<StationReleaseServiceControlResultMessage>
        ExecuteFixedActionAsync(
            BrokerReleaseServiceControlMessage request,
            CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new()
        {
            FileName = SystemctlPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.ArgumentList.Add("--user");
        start.ArgumentList.Add("--no-ask-password");
        start.ArgumentList.Add("--no-pager");
        start.ArgumentList.Add("--plain");
        start.ArgumentList.Add(request.Action);
        start.ArgumentList.Add(request.UnitIdentity);

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ActionTimeout);
        string outcome;
        bool succeeded;
        try
        {
            int exitCode = await RunProcessAsync(start, timeout.Token);
            succeeded = exitCode == 0;
            outcome = succeeded ? "completed" : "systemd-rejected";
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            succeeded = false;
            outcome = "systemd-timeout";
        }
        catch (Exception exception)
            when (exception is IOException or InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
        {
            succeeded = false;
            outcome = "systemd-unavailable";
        }

        return new StationReleaseServiceControlResultMessage(
            StationMessageTypes.ReleaseServiceControlResult,
            request.CorrelationId,
            request.ReleaseIdentity,
            request.Phase,
            request.Action,
            request.ServiceRole,
            request.UnitIdentity,
            succeeded,
            outcome);
    }

    private static async Task<int> RunProcessAsync(
        ProcessStartInfo start,
        CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new IOException("systemctl did not start.");
        }
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            string output = await stdout;
            string error = await stderr;
            if (output.Length > 4096 || error.Length > 4096)
            {
                throw new InvalidDataException(
                    "systemctl output exceeded its bound.");
            }
            return process.ExitCode;
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw;
        }
    }

    private static string GetRuntimeRoot()
    {
        string value =
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? string.Empty;
        const string prefix = "/run/user/";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length <= prefix.Length ||
            !value[prefix.Length..].All(character =>
                character is >= '0' and <= '9') ||
            !string.Equals(Path.GetFullPath(value), value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "XDG_RUNTIME_DIR is unavailable or unsafe for the updater socket.");
        }
        return value;
    }

    private static void RemoveStaleSocket(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or
                DirectoryNotFoundException)
        {
            return;
        }

        FileInfo info = new(path);
        info.Refresh();
        if ((attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            info.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                "The updater socket path is unsafe.");
        }
        File.Delete(path);
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
                "The updater message length is invalid.");
        }
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        T? value = JsonSerializer.Deserialize<T>(
            payload,
            StationProtocol.JsonOptions);
        return value ?? throw new InvalidDataException(
            "The updater request is empty.");
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
                "The updater response is too large.");
        }
        byte[] lengthBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, payload.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
