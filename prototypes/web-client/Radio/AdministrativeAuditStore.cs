using System.Text.Json;

namespace AetherSDR.Web.Radio;

public static class AdministrativeAuditActions
{
    public const string UpdateRadioPolicy = "radio.policy.update";
    public const string UpdateRadioIdentity = "radio.identity.update";
    public const string ForceDisconnectOperator =
        "radio.operator.force_disconnect";
    public const string CreateStationEnrollmentCode =
        "station.enrollment_code.create";
    public const string EnableStationCredential =
        "station.credential.enable";
    public const string DisableStationCredential =
        "station.credential.disable";
    public const string RevokeStationCredential =
        "station.credential.revoke";
    public const string PrepareReleaseUpdate = "release.update.prepare";
    public const string ActivateReleaseUpdate = "release.update.activate";
    public const string RollbackReleaseUpdate = "release.update.rollback";
}

public static class AdministrativeAuditResults
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed record AdministrativeAuditEvent(
    string EventId,
    DateTimeOffset OccurredAt,
    string ActorId,
    string ActorDisplayName,
    string Action,
    string RadioId,
    string? TargetId,
    string Result,
    string Summary);

public sealed class AdministrativeAuditStore
{
    private const int FileVersion = 1;
    private const int DefaultMaximumEntries = 2_000;
    private readonly object m_gate = new();
    private readonly string m_auditPath;
    private readonly ILogger<AdministrativeAuditStore> m_logger;
    private readonly TimeProvider m_timeProvider;
    private readonly int m_maximumEntries;
    private readonly List<AdministrativeAuditEvent> m_events = [];

    public AdministrativeAuditStore(
        string auditPath,
        ILogger<AdministrativeAuditStore> logger,
        TimeProvider? timeProvider = null,
        int maximumEntries = DefaultMaximumEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditPath);
        if (maximumEntries is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                "The audit history limit must be between 1 and 10000.");
        }

        m_auditPath = Path.GetFullPath(auditPath);
        m_logger = logger;
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_maximumEntries = maximumEntries;
        Load();
    }

    public AdministrativeAuditEvent Record(
        string actorId,
        string actorDisplayName,
        string action,
        string radioId,
        string? targetId,
        string result,
        string summary)
    {
        string normalizedResult = NormalizeResult(result);
        AdministrativeAuditEvent auditEvent = new(
            Guid.NewGuid().ToString("N"),
            m_timeProvider.GetUtcNow(),
            NormalizeValue(actorId, 256, "unknown"),
            NormalizeValue(actorDisplayName, 256, "Unknown administrator"),
            NormalizeValue(action, 128, "unknown"),
            NormalizeValue(radioId, 128, "unknown"),
            NormalizeOptionalValue(targetId, 256),
            normalizedResult,
            NormalizeValue(summary, 512, "No details were recorded."));

        lock (m_gate)
        {
            AdministrativeAuditEvent[] previousEvents = m_events.ToArray();
            m_events.Add(auditEvent);
            if (m_events.Count > m_maximumEntries)
            {
                m_events.RemoveRange(
                    0,
                    m_events.Count - m_maximumEntries);
            }
            try
            {
                Persist();
            }
            catch
            {
                m_events.Clear();
                m_events.AddRange(previousEvents);
                throw;
            }
        }

        m_logger.LogInformation(
            "Administrative action {Action} on {RadioId} by {ActorId}: {Result}",
            auditEvent.Action,
            auditEvent.RadioId,
            auditEvent.ActorId,
            auditEvent.Result);
        return auditEvent;
    }

    public IReadOnlyList<AdministrativeAuditEvent> GetRecent(int limit = 50)
    {
        int normalizedLimit = Math.Clamp(limit, 1, 200);
        lock (m_gate)
        {
            return m_events
                .AsEnumerable()
                .Reverse()
                .Take(normalizedLimit)
                .ToArray();
        }
    }

    private void Load()
    {
        if (!File.Exists(m_auditPath))
        {
            return;
        }

        string json = File.ReadAllText(m_auditPath);
        AdministrativeAuditFile? file =
            JsonSerializer.Deserialize<AdministrativeAuditFile>(
                json,
                JsonOptions());
        if (file is null || file.Version != FileVersion)
        {
            throw new InvalidDataException(
                "The administrative audit file has an unsupported format.");
        }

        AdministrativeAuditEvent[] validEvents = file.Events
            .Select(ValidateLoadedEvent)
            .OrderBy(auditEvent => auditEvent.OccurredAt)
            .ThenBy(auditEvent => auditEvent.EventId, StringComparer.Ordinal)
            .TakeLast(m_maximumEntries)
            .ToArray();
        m_events.AddRange(validEvents);
    }

    private AdministrativeAuditEvent ValidateLoadedEvent(
        AdministrativeAuditEvent auditEvent)
    {
        if (auditEvent.OccurredAt == default)
        {
            throw new InvalidDataException(
                "An administrative audit event has no timestamp.");
        }

        return auditEvent with
        {
            EventId = NormalizeValue(auditEvent.EventId, 64, "unknown"),
            ActorId = NormalizeValue(auditEvent.ActorId, 256, "unknown"),
            ActorDisplayName = NormalizeValue(
                auditEvent.ActorDisplayName,
                256,
                "Unknown administrator"),
            Action = NormalizeValue(auditEvent.Action, 128, "unknown"),
            RadioId = NormalizeValue(auditEvent.RadioId, 128, "unknown"),
            TargetId = NormalizeOptionalValue(auditEvent.TargetId, 256),
            Result = NormalizeResult(auditEvent.Result),
            Summary = NormalizeValue(
                auditEvent.Summary,
                512,
                "No details were recorded.")
        };
    }

    private void Persist()
    {
        string? directory = Path.GetDirectoryName(m_auditPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The administrative audit path must include a directory.");
        }

        Directory.CreateDirectory(directory);
        SetDirectoryPermissions(directory);
        string temporaryPath =
            $"{m_auditPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            AdministrativeAuditFile file = new(
                FileVersion,
                m_events.ToArray());
            using (FileStream stream = CreateAuditFile(temporaryPath))
            {
                JsonSerializer.Serialize(stream, file, JsonOptions());
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, m_auditPath, overwrite: true);
            SetFilePermissions(m_auditPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static FileStream CreateAuditFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
        }

        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
                UnixCreateMode =
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite
            });
    }

    private static void SetDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    private static void SetFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
        }
    }

    private static string NormalizeResult(string? result)
    {
        string normalized = result?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            AdministrativeAuditResults.Succeeded =>
                AdministrativeAuditResults.Succeeded,
            AdministrativeAuditResults.Failed =>
                AdministrativeAuditResults.Failed,
            _ => throw new ArgumentException(
                "Audit result must be either 'succeeded' or 'failed'.",
                nameof(result))
        };
    }

    private static string NormalizeValue(
        string? value,
        int maximumLength,
        string fallback)
    {
        string normalized = new(
            (value ?? string.Empty)
                .Where(character => !char.IsControl(character))
                .Take(maximumLength)
                .ToArray());
        normalized = normalized.Trim();
        return normalized.Length > 0 ? normalized : fallback;
    }

    private static string? NormalizeOptionalValue(
        string? value,
        int maximumLength)
    {
        string normalized = NormalizeValue(value, maximumLength, string.Empty);
        return normalized.Length > 0 ? normalized : null;
    }

    private static JsonSerializerOptions JsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private sealed record AdministrativeAuditFile(
        int Version,
        IReadOnlyList<AdministrativeAuditEvent> Events);
}
