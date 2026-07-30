using AetherSDR.Web.Auth;

namespace AetherSDR.Web.Radio;

public sealed class RadioPresenceRegistry
{
    private readonly object m_gate = new();
    private readonly Dictionary<PresenceKey, PresenceEntry> m_connections = [];

    public IReadOnlyList<OperatorPresenceSnapshot> Preview(
        string radioId,
        PresenceSnapshot joiningConnection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioId);
        ArgumentNullException.ThrowIfNull(joiningConnection);

        lock (m_gate)
        {
            return BuildSnapshot(
                m_connections.Values
                    .Where(entry => IsSameRadio(entry.RadioId, radioId))
                    .Select(entry => entry.Connection.ToPresence())
                    .Append(joiningConnection));
        }
    }

    public IReadOnlyList<OperatorPresenceSnapshot> GetSnapshot(string radioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioId);

        lock (m_gate)
        {
            return BuildSnapshot(
                m_connections.Values
                    .Where(entry => IsSameRadio(entry.RadioId, radioId))
                    .Select(entry => entry.Connection.ToPresence()));
        }
    }

    public void Register(
        string radioId,
        RadioCoordinator coordinator,
        RadioClientConnection connection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioId);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(connection);

        lock (m_gate)
        {
            PresenceKey key = new(radioId, connection.ClientId);
            if (!m_connections.TryAdd(
                    key,
                    new PresenceEntry(radioId, coordinator, connection)))
            {
                throw new InvalidOperationException(
                    "Could not register radio-wide browser presence.");
            }
        }

        Broadcast(radioId);
    }

    public void Unregister(string radioId, string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        bool removed;
        lock (m_gate)
        {
            removed = m_connections.Remove(new PresenceKey(radioId, clientId));
        }

        if (removed)
        {
            Broadcast(radioId);
        }
    }

    public int ForceDisconnect(string radioId, string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        PresenceEntry[] connections;
        lock (m_gate)
        {
            connections = m_connections.Values
                .Where(entry =>
                    IsSameRadio(entry.RadioId, radioId) &&
                    string.Equals(
                        entry.Connection.UserId,
                        userId,
                        StringComparison.Ordinal))
                .ToArray();
        }

        foreach (PresenceEntry entry in connections)
        {
            entry.Coordinator.SendJson(
                entry.Connection,
                new
                {
                    @event = "admin.disconnected",
                    reason =
                        "An administrator released this radio session."
                });
            entry.Connection.Complete();
        }

        return connections.Length;
    }

    private void Broadcast(string radioId)
    {
        PresenceEntry[] recipients;
        IReadOnlyList<OperatorPresenceSnapshot> snapshot;
        lock (m_gate)
        {
            recipients = m_connections.Values
                .Where(entry => IsSameRadio(entry.RadioId, radioId))
                .ToArray();
            snapshot = BuildSnapshot(
                recipients.Select(entry => entry.Connection.ToPresence()));
        }

        object message = new
        {
            @event = "presence",
            clients = snapshot
        };
        foreach (PresenceEntry recipient in recipients)
        {
            recipient.Coordinator.SendJson(recipient.Connection, message);
        }
    }

    private static IReadOnlyList<OperatorPresenceSnapshot> BuildSnapshot(
        IEnumerable<PresenceSnapshot> connections)
    {
        return connections
            .GroupBy(connection => connection.UserId, StringComparer.Ordinal)
            .Select(group =>
            {
                PresenceSnapshot earliest = group
                    .OrderBy(connection => connection.ConnectedAt)
                    .First();
                string[] roles = AetherRoles.All
                    .Where(role => group.Any(connection =>
                        connection.Roles.Contains(
                            role,
                            StringComparer.Ordinal)))
                    .ToArray();
                return new OperatorPresenceSnapshot(
                    earliest.UserId,
                    earliest.DisplayName,
                    roles,
                    earliest.ConnectedAt,
                    group.Count());
            })
            .OrderBy(presence => presence.ConnectedAt)
            .ThenBy(presence => presence.UserId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsSameRadio(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private readonly record struct PresenceKey(
        string RadioId,
        string ClientId);

    private sealed record PresenceEntry(
        string RadioId,
        RadioCoordinator Coordinator,
        RadioClientConnection Connection);
}
