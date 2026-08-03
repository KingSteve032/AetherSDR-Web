using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherSDR.Web.Setup;

public sealed class InstallationSetupStore
{
    private const UnixFileMode StateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode StateDirectoryMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;

    private readonly string m_statePath;
    private readonly TimeProvider m_timeProvider;
    private readonly SemaphoreSlim m_gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public InstallationSetupStore(
        string statePath,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(statePath) ||
            !Path.IsPathRooted(statePath))
        {
            throw new InvalidOperationException(
                "The installation setup state path must be absolute.");
        }

        m_statePath = Path.GetFullPath(statePath);
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string StatePath => m_statePath;

    public async Task<InstallationSetupState> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(m_statePath))
            {
                throw new FileNotFoundException(
                    "Installation setup state does not exist.",
                    m_statePath);
            }

            return await ReadAsync(cancellationToken);
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<InstallationSetupState> LoadOrCreateAsync(
        CancellationToken cancellationToken = default)
    {
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(m_statePath))
            {
                return await ReadAsync(cancellationToken);
            }

            DateTimeOffset now = m_timeProvider.GetUtcNow();
            InstallationSetupState initial =
                InstallationSetupState.CreateInitial(now);
            InstallationSetupStateValidator.Validate(initial);
            await WriteAsync(initial, cancellationToken);
            return initial;
        }
        finally
        {
            m_gate.Release();
        }
    }

    public async Task<InstallationSetupState> UpdateAsync(
        long expectedRevision,
        Func<InstallationSetupState, InstallationSetupState> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await m_gate.WaitAsync(cancellationToken);
        try
        {
            InstallationSetupState current;
            if (File.Exists(m_statePath))
            {
                current = await ReadAsync(cancellationToken);
            }
            else
            {
                DateTimeOffset createdAt = m_timeProvider.GetUtcNow();
                current = InstallationSetupState.CreateInitial(createdAt);
            }

            if (current.Revision != expectedRevision)
            {
                throw new InstallationSetupConcurrencyException(
                    expectedRevision,
                    current.Revision);
            }

            InstallationSetupState requested =
                update(current) ??
                throw new InvalidOperationException(
                    "The installation setup update returned no state.");
            if (requested.SchemaVersion != current.SchemaVersion ||
                requested.CreatedAt != current.CreatedAt)
            {
                throw new InvalidOperationException(
                    "Installation setup updates cannot replace schema or creation identity.");
            }

            DateTimeOffset updatedAt = m_timeProvider.GetUtcNow();
            if (updatedAt < current.UpdatedAt)
            {
                updatedAt = current.UpdatedAt;
            }
            InstallationSetupState next = requested with
            {
                Revision = checked(current.Revision + 1),
                UpdatedAt = updatedAt
            };
            InstallationSetupStateValidator.Validate(next);
            await WriteAsync(next, cancellationToken);
            return next;
        }
        finally
        {
            m_gate.Release();
        }
    }

    private async Task<InstallationSetupState> ReadAsync(
        CancellationToken cancellationToken)
    {
        ValidateExistingPermissions();
        await using FileStream stream = new(
            m_statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        InstallationSetupState state;
        try
        {
            state =
                await JsonSerializer.DeserializeAsync<InstallationSetupState>(
                    stream,
                    JsonOptions,
                    cancellationToken) ??
                throw new InvalidOperationException(
                    "Installation setup state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Installation setup state is malformed or contains unknown fields.",
                exception);
        }

        InstallationSetupStateValidator.Validate(state);
        return state;
    }

    private async Task WriteAsync(
        InstallationSetupState state,
        CancellationToken cancellationToken)
    {
        string directory =
            Path.GetDirectoryName(m_statePath) ??
            throw new InvalidOperationException(
                "Installation setup state requires a parent directory.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, StateDirectoryMode);
        }

        string temporaryPath =
            $"{m_statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = StateFileMode;
            }

            await using (FileStream stream = new(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, StateFileMode);
            }

            File.Move(temporaryPath, m_statePath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(m_statePath, StateFileMode);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void ValidateExistingPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode fileMode = File.GetUnixFileMode(m_statePath);
        if (fileMode != StateFileMode)
        {
            throw new InvalidOperationException(
                "Installation setup state must have mode 0600.");
        }

        string directory =
            Path.GetDirectoryName(m_statePath) ??
            throw new InvalidOperationException(
                "Installation setup state requires a parent directory.");
        UnixFileMode directoryMode = File.GetUnixFileMode(directory);
        if (directoryMode != StateDirectoryMode)
        {
            throw new InvalidOperationException(
                "Installation setup state directory must have mode 0700.");
        }
    }
}

public sealed class InstallationSetupConcurrencyException(
    long expectedRevision,
    long actualRevision)
    : InvalidOperationException(
        $"Installation setup state revision changed from " +
        $"{expectedRevision} to {actualRevision}.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
