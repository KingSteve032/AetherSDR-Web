using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Broker;

public static class StationCredentialStates
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string Revoked = "revoked";
}

public static class StationCredentialSources
{
    public const string Imported = "imported";
    public const string Enrolled = "enrolled";
}

public sealed record StationCredentialSnapshot(
    string StationId,
    string State,
    string Source,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? RotatedAt,
    DateTimeOffset UpdatedAt);

public sealed record StationEnrollmentCodeResult(
    string StationId,
    string EnrollmentCode,
    string Purpose,
    DateTimeOffset ExpiresAt);

public sealed record StationEnrollmentResult(
    string StationId,
    string State,
    string Purpose,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? RotatedAt);

public sealed record CreateStationEnrollmentRequest(string StationId);

public sealed record RedeemStationEnrollmentRequest(
    string EnrollmentCode,
    string CredentialSha256);

public sealed class StationEnrollmentException(
    string code,
    string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class StationEnrollmentRegistry
{
    private const int FileVersion = 1;
    private const int MaximumStations = 256;
    private const int MaximumPendingCodes = 128;
    private readonly object m_gate = new();
    private readonly StationLinkSettings m_settings;
    private readonly ILogger<StationEnrollmentRegistry> m_logger;
    private readonly TimeProvider m_timeProvider;
    private readonly string? m_registryPath;
    private readonly Dictionary<string, StationCredentialRecord> m_stations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingEnrollmentRecord> m_pending =
        new(StringComparer.Ordinal);

    public StationEnrollmentRegistry(
        IOptions<StationLinkSettings> settings,
        ILogger<StationEnrollmentRegistry> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        m_settings = settings.Value;
        m_logger = logger;
        m_timeProvider = timeProvider ?? TimeProvider.System;
        m_registryPath = string.IsNullOrWhiteSpace(
            m_settings.EnrollmentRegistryPath)
            ? null
            : Path.GetFullPath(m_settings.EnrollmentRegistryPath);
        Load();
        ImportConfiguredStations();
    }

    public IReadOnlyList<StationCredentialSnapshot> GetSnapshot()
    {
        lock (m_gate)
        {
            return m_stations.Values
                .Select(ToSnapshot)
                .OrderBy(station => station.StationId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public StationEnrollmentCodeResult CreateEnrollmentCode(
        string? stationId)
    {
        if (!StationProtocolValidator.IsIdentifier(
                stationId,
                StationProtocol.MaximumStationIdLength))
        {
            throw new StationEnrollmentException(
                "invalid_station",
                "The station ID is invalid.");
        }

        lock (m_gate)
        {
            return ApplyPersistedMutation(
                () =>
                {
                    DateTimeOffset now = m_timeProvider.GetUtcNow();
                    PruneExpiredCodes(now);
                    if (m_pending.Count >= MaximumPendingCodes)
                    {
                        throw new StationEnrollmentException(
                            "enrollment_capacity",
                            "Too many station enrollment codes are pending.");
                    }

                    foreach (string pendingDigest in m_pending
                                 .Where(item => string.Equals(
                                     item.Value.StationId,
                                     stationId,
                                     StringComparison.Ordinal))
                                 .Select(item => item.Key)
                                 .ToArray())
                    {
                        m_pending.Remove(pendingDigest);
                    }

                    string purpose = m_stations.TryGetValue(
                        stationId!,
                        out StationCredentialRecord? existing)
                        ? existing.State == StationCredentialStates.Revoked
                            ? "reenroll"
                            : "rotate"
                        : "enroll";
                    string code = Convert.ToHexStringLower(
                        RandomNumberGenerator.GetBytes(32));
                    string codeDigest = HashSecret(code);
                    DateTimeOffset expiresAt = now.AddMinutes(
                        m_settings.EnrollmentCodeMinutes);
                    m_pending.Add(
                        codeDigest,
                        new PendingEnrollmentRecord(
                            codeDigest,
                            stationId!,
                            purpose,
                            now,
                            expiresAt));
                    return new StationEnrollmentCodeResult(
                        stationId!,
                        code,
                        purpose,
                        expiresAt);
                });
        }
    }

    public StationEnrollmentResult Redeem(
        string? enrollmentCode,
        string? credentialSha256)
    {
        if (!IsEnrollmentCode(enrollmentCode) ||
            !IsVerifier(credentialSha256))
        {
            throw new StationEnrollmentException(
                "invalid_enrollment",
                "The enrollment request is invalid.");
        }

        lock (m_gate)
        {
            StationEnrollmentResult result = ApplyPersistedMutation(
                () =>
                {
                    DateTimeOffset now = m_timeProvider.GetUtcNow();
                    PruneExpiredCodes(now);
                    string codeDigest = HashSecret(enrollmentCode!);
                    if (!m_pending.Remove(
                            codeDigest,
                            out PendingEnrollmentRecord? pending) ||
                        pending.ExpiresAt <= now)
                    {
                        throw new StationEnrollmentException(
                            "invalid_enrollment",
                            "The enrollment code is invalid or has expired.");
                    }

                    bool rotating = m_stations.TryGetValue(
                        pending.StationId,
                        out StationCredentialRecord? previous);
                    DateTimeOffset enrolledAt = rotating
                        ? previous!.EnrolledAt
                        : now;
                    StationCredentialRecord next = new(
                        pending.StationId,
                        credentialSha256!.ToLowerInvariant(),
                        StationCredentialStates.Enabled,
                        StationCredentialSources.Enrolled,
                        enrolledAt,
                        rotating ? now : null,
                        now);
                    m_stations[pending.StationId] = next;
                    return new StationEnrollmentResult(
                        next.StationId,
                        next.State,
                        pending.Purpose,
                        next.EnrolledAt,
                        next.RotatedAt);
                });
            m_logger.LogInformation(
                "Station {StationId} redeemed a one-time {Purpose} code",
                result.StationId,
                result.Purpose);
            return result;
        }
    }

    public StationCredentialSnapshot SetState(
        string? stationId,
        string state)
    {
        if (!StationProtocolValidator.IsIdentifier(
                stationId,
                StationProtocol.MaximumStationIdLength) ||
            state is not (
                StationCredentialStates.Enabled or
                StationCredentialStates.Disabled or
                StationCredentialStates.Revoked))
        {
            throw new StationEnrollmentException(
                "invalid_station_action",
                "The station credential action is invalid.");
        }

        lock (m_gate)
        {
            StationCredentialSnapshot result = ApplyPersistedMutation(
                () =>
                {
                    if (!m_stations.TryGetValue(
                            stationId!,
                            out StationCredentialRecord? current))
                    {
                        throw new StationEnrollmentException(
                            "station_not_found",
                            "That enrolled station was not found.");
                    }
                    if (state == StationCredentialStates.Enabled &&
                        (current.State == StationCredentialStates.Revoked ||
                         !IsVerifier(current.CredentialSha256)))
                    {
                        throw new StationEnrollmentException(
                            "station_revoked",
                            "A revoked station must use a new enrollment " +
                            "code.");
                    }

                    DateTimeOffset now = m_timeProvider.GetUtcNow();
                    StationCredentialRecord next = current with
                    {
                        CredentialSha256 =
                            state == StationCredentialStates.Revoked
                                ? null
                                : current.CredentialSha256,
                        State = state,
                        UpdatedAt = now
                    };
                    m_stations[stationId!] = next;
                    foreach (string digest in m_pending
                                 .Where(item => string.Equals(
                                     item.Value.StationId,
                                     stationId,
                                     StringComparison.Ordinal))
                                 .Select(item => item.Key)
                                 .ToArray())
                    {
                        m_pending.Remove(digest);
                    }
                    return ToSnapshot(next);
                });
            m_logger.LogInformation(
                "Station {StationId} credential state changed to {State}",
                stationId,
                state);
            return result;
        }
    }

    public bool TryGetVerifier(
        string stationId,
        out byte[]? verifier)
    {
        verifier = null;
        lock (m_gate)
        {
            return m_stations.TryGetValue(
                       stationId,
                       out StationCredentialRecord? station) &&
                   station.State == StationCredentialStates.Enabled &&
                   TryDecodeVerifier(
                       station.CredentialSha256,
                       out verifier);
        }
    }

    public static bool IsVerifier(string? value) =>
        TryDecodeVerifier(value, out _);

    private void Load()
    {
        if (m_registryPath is null || !File.Exists(m_registryPath))
        {
            return;
        }

        StationEnrollmentFile? file =
            JsonSerializer.Deserialize<StationEnrollmentFile>(
                File.ReadAllText(m_registryPath),
                JsonOptions());
        if (file is null ||
            file.Version != FileVersion ||
            file.Stations.Count > MaximumStations ||
            file.Pending.Count > MaximumPendingCodes)
        {
            throw new InvalidDataException(
                "The station enrollment registry has an unsupported format.");
        }

        foreach (StationCredentialRecord station in file.Stations)
        {
            ValidateLoadedStation(station);
            if (!m_stations.TryAdd(station.StationId, station))
            {
                throw new InvalidDataException(
                    "The station enrollment registry contains duplicates.");
            }
        }
        foreach (PendingEnrollmentRecord pending in file.Pending)
        {
            ValidateLoadedPending(pending);
            if (!m_pending.TryAdd(
                    pending.CodeSha256,
                    pending))
            {
                throw new InvalidDataException(
                    "The station enrollment registry contains duplicate codes.");
            }
        }
        PruneExpiredCodes(m_timeProvider.GetUtcNow());
    }

    private void ImportConfiguredStations()
    {
        bool changed = false;
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        HashSet<string> configuredIds = new(StringComparer.Ordinal);
        lock (m_gate)
        {
            foreach (StationCredentialSettings configured in
                     m_settings.Stations ?? [])
            {
                if (!StationProtocolValidator.IsIdentifier(
                        configured.StationId,
                        StationProtocol.MaximumStationIdLength) ||
                    !IsVerifier(configured.CredentialSha256) ||
                    !configuredIds.Add(configured.StationId))
                {
                    throw new InvalidOperationException(
                        "Every configured station requires a unique valid ID " +
                        "and a 64-character SHA-256 credential verifier.");
                }
                if (m_stations.ContainsKey(configured.StationId))
                {
                    continue;
                }
                if (m_stations.Count >= MaximumStations)
                {
                    throw new InvalidOperationException(
                        "The station enrollment registry is full.");
                }
                m_stations.Add(
                    configured.StationId,
                    new StationCredentialRecord(
                        configured.StationId,
                        configured.CredentialSha256.ToLowerInvariant(),
                        StationCredentialStates.Enabled,
                        StationCredentialSources.Imported,
                        now,
                        null,
                        now));
                changed = true;
            }
            if (changed)
            {
                Persist();
            }
        }
    }

    private void PruneExpiredCodes(DateTimeOffset now)
    {
        foreach (string digest in m_pending
                     .Where(item => item.Value.ExpiresAt <= now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            m_pending.Remove(digest);
        }
    }

    private void Persist()
    {
        if (m_registryPath is null)
        {
            return;
        }
        string? directory = Path.GetDirectoryName(m_registryPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The enrollment registry path must include a directory.");
        }

        Directory.CreateDirectory(directory);
        SetDirectoryPermissions(directory);
        string temporaryPath =
            $"{m_registryPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            StationEnrollmentFile file = new(
                FileVersion,
                m_stations.Values
                    .OrderBy(item => item.StationId, StringComparer.Ordinal)
                    .ToArray(),
                m_pending.Values
                    .OrderBy(item => item.ExpiresAt)
                    .ToArray());
            using (FileStream stream = CreateRegistryFile(temporaryPath))
            {
                JsonSerializer.Serialize(stream, file, JsonOptions());
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, m_registryPath, overwrite: true);
            SetFilePermissions(m_registryPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private TResult ApplyPersistedMutation<TResult>(
        Func<TResult> mutation)
    {
        KeyValuePair<string, StationCredentialRecord>[] previousStations =
            m_stations.ToArray();
        KeyValuePair<string, PendingEnrollmentRecord>[] previousPending =
            m_pending.ToArray();
        try
        {
            TResult result = mutation();
            Persist();
            return result;
        }
        catch
        {
            m_stations.Clear();
            foreach ((string key, StationCredentialRecord value) in
                     previousStations)
            {
                m_stations.Add(key, value);
            }
            m_pending.Clear();
            foreach ((string key, PendingEnrollmentRecord value) in
                     previousPending)
            {
                m_pending.Add(key, value);
            }
            throw;
        }
    }

    private static void ValidateLoadedStation(
        StationCredentialRecord station)
    {
        bool verifierRequired =
            station.State is StationCredentialStates.Enabled or
                StationCredentialStates.Disabled;
        if (!StationProtocolValidator.IsIdentifier(
                station.StationId,
                StationProtocol.MaximumStationIdLength) ||
            station.State is not (
                StationCredentialStates.Enabled or
                StationCredentialStates.Disabled or
                StationCredentialStates.Revoked) ||
            station.Source is not (
                StationCredentialSources.Imported or
                StationCredentialSources.Enrolled) ||
            verifierRequired != IsVerifier(station.CredentialSha256) ||
            station.EnrolledAt < DateTimeOffset.UnixEpoch ||
            station.UpdatedAt < station.EnrolledAt ||
            station.RotatedAt < station.EnrolledAt)
        {
            throw new InvalidDataException(
                "The station enrollment registry contains an invalid station.");
        }
    }

    private static void ValidateLoadedPending(
        PendingEnrollmentRecord pending)
    {
        if (!IsVerifier(pending.CodeSha256) ||
            !StationProtocolValidator.IsIdentifier(
                pending.StationId,
                StationProtocol.MaximumStationIdLength) ||
            pending.Purpose is not ("enroll" or "rotate" or "reenroll") ||
            pending.CreatedAt < DateTimeOffset.UnixEpoch ||
            pending.ExpiresAt <= pending.CreatedAt)
        {
            throw new InvalidDataException(
                "The station enrollment registry contains an invalid code.");
        }
    }

    private static StationCredentialSnapshot ToSnapshot(
        StationCredentialRecord station) =>
        new(
            station.StationId,
            station.State,
            station.Source,
            station.EnrolledAt,
            station.RotatedAt,
            station.UpdatedAt);

    private static string HashSecret(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsEnrollmentCode(string? value) =>
        value is { Length: 64 } &&
        value.All(Uri.IsHexDigit);

    private static bool TryDecodeVerifier(
        string? value,
        out byte[]? verifier)
    {
        verifier = null;
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length != 64)
        {
            return false;
        }
        try
        {
            byte[] decoded = Convert.FromHexString(normalized);
            if (decoded.Length != 32)
            {
                return false;
            }
            verifier = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static FileStream CreateRegistryFile(string path)
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

    private static JsonSerializerOptions JsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private sealed record StationEnrollmentFile(
        int Version,
        IReadOnlyList<StationCredentialRecord> Stations,
        IReadOnlyList<PendingEnrollmentRecord> Pending);

    private sealed record StationCredentialRecord(
        string StationId,
        string? CredentialSha256,
        string State,
        string Source,
        DateTimeOffset EnrolledAt,
        DateTimeOffset? RotatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record PendingEnrollmentRecord(
        string CodeSha256,
        string StationId,
        string Purpose,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);
}
