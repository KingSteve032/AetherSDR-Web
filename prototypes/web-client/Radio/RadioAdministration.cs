using System.Text.Json;

namespace AetherSDR.Web.Radio;

public static class RadioAccessModes
{
    public const string Shared = "shared";
    public const string Exclusive = "exclusive";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is Shared or Exclusive;
    }
}

public sealed record RadioAccessPolicySnapshot(
    string RadioId,
    string Mode,
    string? ReservedUserId,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

public sealed record RadioAccessDecision(
    bool Allowed,
    string? Reason);

public sealed record UpdateRadioAccessPolicyRequest(
    string Mode,
    string? ReservedUserId);

public sealed record AdminRadioOperatorSnapshot(
    string UserId,
    string DisplayName,
    int BrowserConnections,
    int RadioSessions,
    DateTimeOffset LastActivity);

public sealed record AdminRadioGuiClientSnapshot(
    uint ClientHandle,
    string ClientId,
    string Program,
    string Station,
    string Source,
    bool LocalPtt,
    bool BrowserOwned,
    string? SessionId,
    string? OperatorName);

public sealed record AdminRadioSnapshot(
    string RadioId,
    string Label,
    string Model,
    string Serial,
    string Host,
    int Port,
    string Source,
    string StationId,
    string SourceRadioId,
    string Status,
    bool Online,
    bool MultiFlexEnabled,
    int AvailableClients,
    int LicensedClients,
    AdminRadioHealthSnapshot Health,
    IReadOnlyList<AdminRadioCapacitySample> CapacityHistory,
    RadioAccessPolicySnapshot Policy,
    RadioOnboardingPolicySnapshot Onboarding,
    IReadOnlyList<AdminRadioGuiClientSnapshot> ConnectedClients,
    IReadOnlyList<AdminRadioOperatorSnapshot> Operators,
    IReadOnlyList<RadioSessionDiagnostics> Sessions);

public sealed record ForceDisconnectResult(
    int BrowserConnections,
    int RadioSessions);

public sealed class RadioAccessDeniedException(
    string radioId,
    string message)
    : InvalidOperationException(message)
{
    public string RadioId { get; } = radioId;
}

public sealed class RadioAccessPolicyStore
{
    private const int FileVersion = 1;
    private readonly object m_gate = new();
    private readonly string m_policyPath;
    private readonly ILogger<RadioAccessPolicyStore> m_logger;
    private readonly Dictionary<string, RadioAccessPolicySnapshot> m_policies =
        new(StringComparer.OrdinalIgnoreCase);

    public RadioAccessPolicyStore(
        string policyPath,
        ILogger<RadioAccessPolicyStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyPath);
        m_policyPath = Path.GetFullPath(policyPath);
        m_logger = logger;
        Load();
    }

    public RadioAccessPolicySnapshot GetPolicy(string radioId)
    {
        string normalizedRadioId = ValidateIdentifier(
            radioId,
            nameof(radioId),
            128);
        lock (m_gate)
        {
            return m_policies.TryGetValue(
                normalizedRadioId,
                out RadioAccessPolicySnapshot? policy)
                ? policy
                : DefaultPolicy(normalizedRadioId);
        }
    }

    public RadioAccessDecision Evaluate(
        string radioId,
        string userId,
        IEnumerable<string> activeUserIds,
        bool administratorBypass)
    {
        ArgumentNullException.ThrowIfNull(activeUserIds);
        string normalizedUserId = ValidateIdentifier(
            userId,
            nameof(userId),
            256);
        if (administratorBypass)
        {
            return new RadioAccessDecision(true, null);
        }

        RadioAccessPolicySnapshot policy = GetPolicy(radioId);
        if (policy.ReservedUserId is not null &&
            !string.Equals(
                policy.ReservedUserId,
                normalizedUserId,
                StringComparison.Ordinal))
        {
            return new RadioAccessDecision(
                false,
                "This radio is reserved for another account.");
        }

        bool anotherUserIsActive = activeUserIds.Any(activeUserId =>
            !string.Equals(
                activeUserId,
                normalizedUserId,
                StringComparison.Ordinal));
        if (string.Equals(
                policy.Mode,
                RadioAccessModes.Exclusive,
                StringComparison.Ordinal) &&
            anotherUserIsActive)
        {
            return new RadioAccessDecision(
                false,
                "This radio is assigned exclusively and is already in use.");
        }

        return new RadioAccessDecision(true, null);
    }

    public RadioAccessPolicySnapshot Update(
        string radioId,
        string? mode,
        string? reservedUserId,
        string updatedBy)
    {
        string normalizedRadioId = ValidateIdentifier(
            radioId,
            nameof(radioId),
            128);
        if (!RadioAccessModes.TryNormalize(mode, out string normalizedMode))
        {
            throw new ArgumentException(
                "Mode must be either 'shared' or 'exclusive'.",
                nameof(mode));
        }

        string? normalizedReservation = NormalizeOptionalIdentifier(
            reservedUserId,
            nameof(reservedUserId),
            256);
        string normalizedUpdatedBy = ValidateIdentifier(
            updatedBy,
            nameof(updatedBy),
            256);
        RadioAccessPolicySnapshot policy = new(
            normalizedRadioId,
            normalizedMode,
            normalizedReservation,
            DateTimeOffset.UtcNow,
            normalizedUpdatedBy);

        lock (m_gate)
        {
            bool hadPreviousPolicy = m_policies.TryGetValue(
                normalizedRadioId,
                out RadioAccessPolicySnapshot? previousPolicy);
            m_policies[normalizedRadioId] = policy;
            try
            {
                Persist();
            }
            catch
            {
                if (hadPreviousPolicy)
                {
                    m_policies[normalizedRadioId] = previousPolicy!;
                }
                else
                {
                    m_policies.Remove(normalizedRadioId);
                }
                throw;
            }
        }

        m_logger.LogInformation(
            "Radio access policy updated for {RadioId}: {Mode}, reserved={Reserved}",
            normalizedRadioId,
            normalizedMode,
            normalizedReservation is not null);
        return policy;
    }

    private void Load()
    {
        if (!File.Exists(m_policyPath))
        {
            return;
        }

        string json = File.ReadAllText(m_policyPath);
        RadioAccessPolicyFile? file = JsonSerializer.Deserialize<RadioAccessPolicyFile>(
            json,
            JsonOptions());
        if (file is null || file.Version != FileVersion)
        {
            throw new InvalidDataException(
                "The radio access policy file has an unsupported format.");
        }

        foreach (RadioAccessPolicySnapshot policy in file.Policies)
        {
            string radioId = ValidateIdentifier(
                policy.RadioId,
                nameof(policy.RadioId),
                128);
            if (!RadioAccessModes.TryNormalize(
                    policy.Mode,
                    out string normalizedMode))
            {
                throw new InvalidDataException(
                    $"Radio '{radioId}' has an invalid access mode.");
            }

            string? reservedUserId = NormalizeOptionalIdentifier(
                policy.ReservedUserId,
                nameof(policy.ReservedUserId),
                256);
            m_policies[radioId] = policy with
            {
                RadioId = radioId,
                Mode = normalizedMode,
                ReservedUserId = reservedUserId
            };
        }
    }

    private void Persist()
    {
        string? directory = Path.GetDirectoryName(m_policyPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The radio access policy path must include a directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath =
            $"{m_policyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            RadioAccessPolicyFile file = new(
                FileVersion,
                m_policies.Values
                    .OrderBy(
                        policy => policy.RadioId,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, JsonOptions());
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, m_policyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static RadioAccessPolicySnapshot DefaultPolicy(string radioId) =>
        new(
            radioId,
            RadioAccessModes.Shared,
            null,
            null,
            null);

    private static string ValidateIdentifier(
        string? value,
        string parameterName,
        int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 ||
            normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A valid {parameterName} is required.",
                parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptionalIdentifier(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ValidateIdentifier(value, parameterName, maximumLength);
    }

    private static JsonSerializerOptions JsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private sealed record RadioAccessPolicyFile(
        int Version,
        IReadOnlyList<RadioAccessPolicySnapshot> Policies);
}

public sealed class RadioAdministrationService(
    RadioSelectionManager radioCatalog,
    RadioCapacityHistoryService capacityHistory,
    RadioSessionRegistry sessions,
    RadioPresenceRegistry presence,
    RadioAccessPolicyStore policies,
    RadioOnboardingPolicyStore onboarding,
    ILogger<RadioAdministrationService> logger)
{
    public IReadOnlyList<AdminRadioSnapshot> GetInventory()
    {
        RadioSelectionOption[] radios =
            radioCatalog.GetSnapshot().Radios.ToArray();
        RadioSessionDiagnostics[] sessionSnapshots =
            sessions.GetDiagnostics().ToArray();

        return radios
            .Select(radio => BuildRadioSnapshot(radio, sessionSnapshots))
            .OrderByDescending(radio => radio.Online)
            .ThenBy(radio => radio.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RadioAccessPolicySnapshot UpdatePolicy(
        string radioId,
        UpdateRadioAccessPolicyRequest request,
        string administratorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureKnownRadio(radioId);
        return policies.Update(
            radioId,
            request.Mode,
            request.ReservedUserId,
            administratorId);
    }

    public RadioOnboardingPolicySnapshot UpdateLabel(
        string radioId,
        UpdateRadioOnboardingRequest request,
        string administratorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureKnownRadio(radioId);
        return onboarding.UpdateLabel(
            radioId,
            request.Label,
            administratorId);
    }

    public async Task<ForceDisconnectResult> ForceDisconnectAsync(
        string radioId,
        string userId)
    {
        EnsureKnownRadio(radioId);
        int browserConnections =
            presence.ForceDisconnect(radioId, userId);
        int radioSessions =
            await sessions.TerminateUserSessionsAsync(radioId, userId);
        logger.LogWarning(
            "Administrator disconnected user {UserId} from radio {RadioId}: " +
            "{BrowserConnections} browser connections, {RadioSessions} radio sessions",
            userId,
            radioId,
            browserConnections,
            radioSessions);
        return new ForceDisconnectResult(
            browserConnections,
            radioSessions);
    }

    private AdminRadioSnapshot BuildRadioSnapshot(
        RadioSelectionOption radio,
        IReadOnlyList<RadioSessionDiagnostics> allSessions)
    {
        RadioSessionDiagnostics[] radioSessions = allSessions
            .Where(session => string.Equals(
                session.RadioId,
                radio.RadioId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        IReadOnlyList<OperatorPresenceSnapshot> radioPresence =
            presence.GetSnapshot(radio.RadioId);
        string[] userIds = radioSessions
            .Select(session => session.UserId)
            .Concat(radioPresence.Select(person => person.UserId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AdminRadioOperatorSnapshot[] operators = userIds
            .Select(userId =>
            {
                OperatorPresenceSnapshot? connected = radioPresence
                    .FirstOrDefault(person => string.Equals(
                        person.UserId,
                        userId,
                        StringComparison.Ordinal));
                RadioSessionDiagnostics[] userSessions = radioSessions
                    .Where(session => string.Equals(
                        session.UserId,
                        userId,
                        StringComparison.Ordinal))
                    .ToArray();
                DateTimeOffset lastActivity = userSessions.Length > 0
                    ? userSessions.Max(session => session.LastActivity)
                    : connected?.ConnectedAt ?? DateTimeOffset.UtcNow;
                string displayName =
                    connected?.DisplayName ??
                    userSessions.FirstOrDefault()?.DisplayName ??
                    userId;
                return new AdminRadioOperatorSnapshot(
                    userId,
                    displayName,
                    connected?.ConnectionCount ?? 0,
                    userSessions.Length,
                    lastActivity);
            })
            .OrderBy(person => person.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(person => person.UserId, StringComparer.Ordinal)
            .ToArray();

        RadioOnboardingPolicySnapshot onboardingPolicy =
            onboarding.GetPolicy(radio.RadioId);
        return new AdminRadioSnapshot(
            radio.RadioId,
            onboardingPolicy.Label ?? radio.Label,
            radio.Model,
            radio.Serial,
            radio.Host,
            radio.Port,
            radio.Source,
            radio.StationId,
            string.IsNullOrWhiteSpace(radio.SourceRadioId)
                ? radio.RadioId
                : radio.SourceRadioId,
            radio.Status,
            radio.Online,
            radio.MultiFlexEnabled,
            radio.AvailableClients,
            radio.LicensedClients,
            RadioHealthClassifier.Classify(
                radio,
                radioSessions,
                DateTimeOffset.UtcNow),
            capacityHistory.GetHistory(radio.RadioId),
            policies.GetPolicy(radio.RadioId),
            onboardingPolicy,
            BuildConnectedClients(radioSessions),
            operators,
            radioSessions
                .OrderBy(session => session.CreatedAt)
                .ToArray());
    }

    internal static IReadOnlyList<AdminRadioGuiClientSnapshot>
        BuildConnectedClients(
            IReadOnlyList<RadioSessionDiagnostics> radioSessions)
    {
        Dictionary<uint, List<RadioGuiClientDiagnostics>> observed = [];
        foreach (RadioSessionDiagnostics session in radioSessions)
        {
            foreach (RadioGuiClientDiagnostics client in
                     session.Transport.GuiClients)
            {
                if (client.ClientHandle == 0)
                {
                    continue;
                }
                if (!observed.TryGetValue(
                        client.ClientHandle,
                        out List<RadioGuiClientDiagnostics>? reports))
                {
                    reports = [];
                    observed.Add(client.ClientHandle, reports);
                }
                reports.Add(client);
            }

            uint ownedHandle = session.Transport.ClientHandle;
            if (ownedHandle != 0 && !observed.ContainsKey(ownedHandle))
            {
                observed.Add(
                    ownedHandle,
                    [
                        new RadioGuiClientDiagnostics(
                            ownedHandle,
                            session.GuiClientId,
                            "AetherSDR",
                            "AETHER-WEB-RX",
                            session.Host,
                            false,
                            true)
                    ]);
            }
        }

        return observed
            .Select(pair =>
            {
                RadioGuiClientDiagnostics client = pair.Value
                    .OrderByDescending(ClientReportScore)
                    .First();
                RadioSessionDiagnostics? ownedSession =
                    radioSessions.FirstOrDefault(session =>
                        session.Transport.ClientHandle == pair.Key);
                return new AdminRadioGuiClientSnapshot(
                    pair.Key,
                    client.ClientId,
                    client.Program,
                    client.Station,
                    client.Source,
                    client.LocalPtt,
                    ownedSession is not null,
                    ownedSession?.SessionId,
                    ownedSession?.DisplayName);
            })
            .OrderByDescending(client => client.BrowserOwned)
            .ThenBy(client => client.Station, StringComparer.OrdinalIgnoreCase)
            .ThenBy(client => client.ClientHandle)
            .ToArray();
    }

    private static int ClientReportScore(
        RadioGuiClientDiagnostics client) =>
        (string.IsNullOrWhiteSpace(client.ClientId) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(client.Program) ||
         string.Equals(
             client.Program,
             "Unknown",
             StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1) +
        (string.IsNullOrWhiteSpace(client.Station) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(client.Source) ? 0 : 1);

    private void EnsureKnownRadio(string radioId)
    {
        bool known = radioCatalog.GetSnapshot().Radios.Any(radio =>
            string.Equals(
                radio.RadioId,
                radioId?.Trim(),
                StringComparison.OrdinalIgnoreCase));
        if (!known)
        {
            throw new KeyNotFoundException(
                "That radio is not in the server inventory.");
        }
    }
}
