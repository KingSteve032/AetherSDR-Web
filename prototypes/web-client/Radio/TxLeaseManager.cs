using System.Security.Cryptography;

namespace AetherSDR.Web.Radio;

public sealed record TxLeaseChange(
    TxLease Lease,
    bool Active,
    string Reason,
    DateTimeOffset OccurredAt);

internal sealed class TxLeaseAdmissionClosureAuthority
{
}

internal sealed record TxLeaseAdmissionClosureObservation(
    bool AdmissionClosed,
    bool DifferentClosureActive,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<TxLease> Leases)
{
    internal bool Drained => AdmissionClosed && Leases.Count == 0;
}

public sealed class TxLeaseManager(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan MinimumLeaseDuration =
        TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumLeaseDuration =
        TimeSpan.FromSeconds(15);

    private readonly object m_gate = new();
    private readonly TimeProvider m_timeProvider =
        timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, TxLease> m_leases =
        new(StringComparer.OrdinalIgnoreCase);
    private TxLeaseAdmissionClosureAuthority? m_admissionClosureAuthority;
    private DateTimeOffset? m_admissionClosedAt;

    public event Action<TxLeaseChange>? Changed;

    public TxLease? GetCurrent(string radioId)
    {
        string normalizedRadioId = NormalizeRadioId(radioId);
        if (normalizedRadioId.Length == 0)
        {
            return null;
        }

        TxLeaseChange? expired = null;
        TxLease? current;
        lock (m_gate)
        {
            expired = ExpireRadioLocked(
                normalizedRadioId,
                m_timeProvider.GetUtcNow());
            m_leases.TryGetValue(normalizedRadioId, out current);
        }
        Publish(expired);
        return current;
    }

    public IReadOnlyList<TxLease> GetSnapshot()
    {
        IReadOnlyList<TxLeaseChange> expired;
        TxLease[] snapshot;
        lock (m_gate)
        {
            expired = ExpireAllLocked(m_timeProvider.GetUtcNow());
            snapshot = m_leases.Values
                .OrderBy(lease => lease.RadioId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        Publish(expired);
        return snapshot;
    }

    internal IReadOnlyList<TxLease> GetObservationSnapshot()
    {
        lock (m_gate)
        {
            return CreateLeaseObservationLocked();
        }
    }

    internal bool TryCloseAdmission(
        TxLeaseAdmissionClosureAuthority authority,
        out TxLeaseAdmissionClosureObservation observation)
    {
        ArgumentNullException.ThrowIfNull(authority);

        lock (m_gate)
        {
            if (m_admissionClosureAuthority is null)
            {
                m_admissionClosureAuthority = authority;
                m_admissionClosedAt = m_timeProvider.GetUtcNow();
            }
            else if (!ReferenceEquals(m_admissionClosureAuthority, authority))
            {
                observation = CreateAdmissionObservationLocked(authority);
                return false;
            }

            observation = CreateAdmissionObservationLocked(authority);
            return true;
        }
    }

    internal TxLeaseAdmissionClosureObservation ObserveAdmissionClosure(
        TxLeaseAdmissionClosureAuthority? authority)
    {
        lock (m_gate)
        {
            return CreateAdmissionObservationLocked(authority);
        }
    }

    public bool TryAcquire(
        string radioId,
        string sessionId,
        string clientId,
        string userId,
        string displayName,
        TimeSpan duration,
        out TxLease? lease,
        out string? error)
    {
        lease = null;
        error = ValidateAcquire(
            radioId,
            sessionId,
            clientId,
            userId,
            displayName,
            duration);
        if (error is not null)
        {
            return false;
        }

        string normalizedRadioId = NormalizeRadioId(radioId);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        TxLeaseChange? expired;
        TxLeaseChange? acquired = null;
        lock (m_gate)
        {
            if (m_admissionClosureAuthority is not null)
            {
                error =
                    "TX lease admission is closed for a verified release activation transaction.";
                return false;
            }

            expired = ExpireRadioLocked(normalizedRadioId, now);
            if (m_leases.TryGetValue(
                    normalizedRadioId,
                    out TxLease? existing))
            {
                lease = existing;
                error = SameOwner(existing, sessionId, clientId)
                    ? "This browser already holds the TX lease; renew it using its lease ID."
                    : $"TX is held by {existing.DisplayName}.";
                return false;
            }

            lease = new TxLease(
                Convert.ToHexStringLower(
                    RandomNumberGenerator.GetBytes(16)),
                normalizedRadioId,
                sessionId,
                clientId,
                userId,
                displayName,
                now,
                now,
                now.Add(duration));
            m_leases.Add(normalizedRadioId, lease);
            acquired = new TxLeaseChange(
                lease,
                Active: true,
                "acquired",
                now);
        }
        Publish(expired);
        Publish(acquired);
        return true;
    }

    public bool TryRenew(
        string radioId,
        string leaseId,
        string sessionId,
        string clientId,
        TimeSpan duration,
        out TxLease? lease,
        out string? error)
    {
        lease = null;
        error = ValidateLeaseIdentity(
            radioId,
            leaseId,
            sessionId,
            clientId,
            duration);
        if (error is not null)
        {
            return false;
        }

        string normalizedRadioId = NormalizeRadioId(radioId);
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        TxLeaseChange? expired;
        TxLeaseChange? renewed = null;
        bool success = false;
        lock (m_gate)
        {
            if (m_admissionClosureAuthority is not null)
            {
                error =
                    "TX lease renewal is closed for a verified release activation transaction.";
                return false;
            }

            expired = ExpireRadioLocked(normalizedRadioId, now);
            if (!m_leases.TryGetValue(
                    normalizedRadioId,
                    out TxLease? existing) ||
                !Matches(existing, leaseId, sessionId, clientId))
            {
                error = "The TX lease is missing, expired, or owned by another browser.";
            }
            else
            {
                lease = existing with
                {
                    RenewedAt = now,
                    ExpiresAt = now.Add(duration)
                };
                m_leases[normalizedRadioId] = lease;
                renewed = new TxLeaseChange(
                    lease,
                    Active: true,
                    "renewed",
                    now);
                success = true;
            }
        }
        Publish(expired);
        Publish(renewed);
        return success;
    }

    public bool TryValidate(
        string radioId,
        string leaseId,
        string sessionId,
        string clientId,
        out TxLease? lease,
        out string? error)
    {
        lease = null;
        error = ValidateLeaseIdentity(
            radioId,
            leaseId,
            sessionId,
            clientId,
            MinimumLeaseDuration);
        if (error is not null)
        {
            return false;
        }

        string normalizedRadioId = NormalizeRadioId(radioId);
        TxLeaseChange? expired;
        bool valid = false;
        lock (m_gate)
        {
            expired = ExpireRadioLocked(
                normalizedRadioId,
                m_timeProvider.GetUtcNow());
            if (!m_leases.TryGetValue(
                    normalizedRadioId,
                    out lease) ||
                !Matches(lease, leaseId, sessionId, clientId))
            {
                lease = null;
                error = "A current TX lease held by this browser is required.";
            }
            else
            {
                valid = true;
            }
        }
        Publish(expired);
        return valid;
    }

    public bool TryRelease(
        string radioId,
        string leaseId,
        string sessionId,
        string clientId,
        string reason,
        out TxLease? released)
    {
        released = null;
        string normalizedRadioId = NormalizeRadioId(radioId);
        if (normalizedRadioId.Length == 0 ||
            !ValidIdentifier(leaseId, 64) ||
            !ValidIdentifier(sessionId, 128) ||
            !ValidIdentifier(clientId, 128) ||
            !ValidReason(reason))
        {
            return false;
        }

        DateTimeOffset now = m_timeProvider.GetUtcNow();
        TxLeaseChange? expired;
        TxLeaseChange? change = null;
        bool success = false;
        lock (m_gate)
        {
            expired = ExpireRadioLocked(normalizedRadioId, now);
            if (m_leases.TryGetValue(
                    normalizedRadioId,
                    out TxLease? existing) &&
                Matches(existing, leaseId, sessionId, clientId))
            {
                m_leases.Remove(normalizedRadioId);
                released = existing;
                change = new TxLeaseChange(
                    existing,
                    Active: false,
                    reason,
                    now);
                success = true;
            }
        }
        Publish(expired);
        Publish(change);
        return success;
    }

    public bool TryReleaseOwner(
        string radioId,
        string sessionId,
        string clientId,
        string reason,
        out TxLease? released)
    {
        released = null;
        string normalizedRadioId = NormalizeRadioId(radioId);
        if (normalizedRadioId.Length == 0 ||
            !ValidIdentifier(sessionId, 128) ||
            !ValidIdentifier(clientId, 128) ||
            !ValidReason(reason))
        {
            return false;
        }

        DateTimeOffset now = m_timeProvider.GetUtcNow();
        TxLeaseChange? expired;
        TxLeaseChange? change = null;
        bool success = false;
        lock (m_gate)
        {
            expired = ExpireRadioLocked(normalizedRadioId, now);
            if (m_leases.TryGetValue(
                    normalizedRadioId,
                    out TxLease? existing) &&
                SameOwner(existing, sessionId, clientId))
            {
                m_leases.Remove(normalizedRadioId);
                released = existing;
                change = new TxLeaseChange(
                    existing,
                    Active: false,
                    reason,
                    now);
                success = true;
            }
        }
        Publish(expired);
        Publish(change);
        return success;
    }

    public int ReleaseSession(string sessionId, string reason)
    {
        if (!ValidIdentifier(sessionId, 128) || !ValidReason(reason))
        {
            return 0;
        }

        List<TxLeaseChange> changes = [];
        lock (m_gate)
        {
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            foreach (KeyValuePair<string, TxLease> entry in
                m_leases
                    .Where(entry => string.Equals(
                        entry.Value.SessionId,
                        sessionId,
                        StringComparison.Ordinal))
                    .ToArray())
            {
                m_leases.Remove(entry.Key);
                changes.Add(new TxLeaseChange(
                    entry.Value,
                    Active: false,
                    reason,
                    now));
            }
        }
        Publish(changes);
        return changes.Count;
    }

    public int SweepExpired()
    {
        IReadOnlyList<TxLeaseChange> expired;
        lock (m_gate)
        {
            expired = ExpireAllLocked(m_timeProvider.GetUtcNow());
        }
        Publish(expired);
        return expired.Count;
    }

    private TxLease[] CreateLeaseObservationLocked() =>
        m_leases.Values
            .OrderBy(lease => lease.RadioId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(lease => lease.LeaseId, StringComparer.Ordinal)
            .ToArray();

    private TxLeaseAdmissionClosureObservation CreateAdmissionObservationLocked(
        TxLeaseAdmissionClosureAuthority? authority)
    {
        bool closureActive = m_admissionClosureAuthority is not null;
        bool exactClosure = closureActive &&
            authority is not null &&
            ReferenceEquals(m_admissionClosureAuthority, authority);
        return new TxLeaseAdmissionClosureObservation(
            exactClosure,
            closureActive && !exactClosure,
            exactClosure ? m_admissionClosedAt : null,
            CreateLeaseObservationLocked());
    }

    private TxLeaseChange? ExpireRadioLocked(
        string normalizedRadioId,
        DateTimeOffset now)
    {
        if (!m_leases.TryGetValue(
                normalizedRadioId,
                out TxLease? existing) ||
            existing.ExpiresAt > now)
        {
            return null;
        }

        m_leases.Remove(normalizedRadioId);
        return new TxLeaseChange(
            existing,
            Active: false,
            "expired",
            now);
    }

    private IReadOnlyList<TxLeaseChange> ExpireAllLocked(DateTimeOffset now)
    {
        List<TxLeaseChange> changes = [];
        foreach (KeyValuePair<string, TxLease> entry in
            m_leases
                .Where(entry => entry.Value.ExpiresAt <= now)
                .ToArray())
        {
            m_leases.Remove(entry.Key);
            changes.Add(new TxLeaseChange(
                entry.Value,
                Active: false,
                "expired",
                now));
        }
        return changes;
    }

    private static string? ValidateAcquire(
        string radioId,
        string sessionId,
        string clientId,
        string userId,
        string displayName,
        TimeSpan duration)
    {
        if (NormalizeRadioId(radioId).Length == 0 ||
            !ValidIdentifier(sessionId, 128) ||
            !ValidIdentifier(clientId, 128) ||
            !ValidIdentifier(userId, 256) ||
            string.IsNullOrWhiteSpace(displayName) ||
            displayName.Length > 256 ||
            displayName.Any(char.IsControl))
        {
            return "A valid radio, session, browser, and operator identity are required.";
        }
        return ValidateDuration(duration);
    }

    private static string? ValidateLeaseIdentity(
        string radioId,
        string leaseId,
        string sessionId,
        string clientId,
        TimeSpan duration)
    {
        if (NormalizeRadioId(radioId).Length == 0 ||
            !ValidIdentifier(leaseId, 64) ||
            !ValidIdentifier(sessionId, 128) ||
            !ValidIdentifier(clientId, 128))
        {
            return "A valid radio, lease, session, and browser identity are required.";
        }
        return ValidateDuration(duration);
    }

    private static string? ValidateDuration(TimeSpan duration) =>
        duration < MinimumLeaseDuration || duration > MaximumLeaseDuration
            ? $"TX lease duration must be between " +
              $"{MinimumLeaseDuration.TotalSeconds:0} and " +
              $"{MaximumLeaseDuration.TotalSeconds:0} seconds."
            : null;

    private static string NormalizeRadioId(string? radioId)
    {
        string normalized = radioId?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 128 &&
               normalized.All(character => !char.IsControl(character))
            ? normalized.ToUpperInvariant()
            : string.Empty;
    }

    private static bool ValidIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value.All(character =>
            !char.IsControl(character) &&
            !char.IsWhiteSpace(character));

    private static bool ValidReason(string? reason) =>
        reason is { Length: > 0 and <= 64 } &&
        reason.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');

    private static bool SameOwner(
        TxLease lease,
        string sessionId,
        string clientId) =>
        string.Equals(lease.SessionId, sessionId, StringComparison.Ordinal) &&
        string.Equals(lease.ClientId, clientId, StringComparison.Ordinal);

    private static bool Matches(
        TxLease lease,
        string leaseId,
        string sessionId,
        string clientId) =>
        string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal) &&
        SameOwner(lease, sessionId, clientId);

    private void Publish(TxLeaseChange? change)
    {
        if (change is not null)
        {
            Publish([change]);
        }
    }

    private void Publish(IEnumerable<TxLeaseChange> changes)
    {
        Action<TxLeaseChange>? changed = Changed;
        if (changed is null)
        {
            return;
        }

        Delegate[] subscribers = changed.GetInvocationList();
        foreach (TxLeaseChange change in changes)
        {
            foreach (Delegate subscriber in subscribers)
            {
                try
                {
                    ((Action<TxLeaseChange>)subscriber)(change);
                }
                catch
                {
                    // Lease arbitration must remain available even when a
                    // diagnostic subscriber has already begun shutting down.
                }
            }
        }
    }
}

public sealed class TxLeaseWatchdogService(
    TxLeaseManager leases,
    ILogger<TxLeaseWatchdogService> logger)
    : BackgroundService
{
    private static readonly TimeSpan SweepInterval =
        TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            int expired = leases.SweepExpired();
            if (expired > 0)
            {
                logger.LogWarning(
                    "Expired {LeaseCount} TX lease(s); every matching keying path must force unkey",
                    expired);
            }
        }
    }
}
