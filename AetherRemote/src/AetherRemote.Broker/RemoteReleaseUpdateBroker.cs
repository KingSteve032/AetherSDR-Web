using System.Security.Cryptography;
using System.Text.Json;
using AetherRemote.Protocol;

namespace AetherRemote.Broker;

public sealed record RemoteReleaseUpdateRequest(
    string StationId,
    string ReleaseIdentity);

public sealed record RemoteReleaseUpdateResult(
    string StationId,
    string CorrelationId,
    string ReleaseIdentity,
    bool Succeeded,
    string Outcome,
    string ActiveReleaseIdentity,
    bool RolledBack);

public sealed class RemoteReleaseUpdateException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Correlates one exact signed-release identity with one connected station. The
/// request deliberately has no URL, path, shell, executable, service name, or
/// arbitrary payload. Download, signature verification, staging, switching,
/// health verification, and rollback remain station-local responsibilities.
/// </summary>
public sealed class RemoteReleaseUpdateBroker
{
    private const int MaximumRecentRequests = 64;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RecentRequestLifetime =
        TimeSpan.FromMinutes(10);
    private readonly object m_gate = new();
    private readonly Dictionary<string, StationLink> m_links =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingRequest> m_pending =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecentRequest> m_recent =
        new(StringComparer.Ordinal);

    public StationUpdateLease AttachStation(
        string stationId,
        string connectionId,
        IReadOnlyList<string> capabilities,
        Func<object, CancellationToken, Task> sender)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(sender);
        lock (m_gate)
        {
            m_links[stationId] = new StationLink(
                connectionId,
                capabilities.ToArray(),
                sender);
        }
        return new StationUpdateLease(this, stationId, connectionId);
    }

    public async Task<RemoteReleaseUpdateResult> ExecuteAsync(
        RemoteReleaseUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StationProtocolValidator.IsIdentifier(
                request.StationId,
                StationProtocol.MaximumStationIdLength))
        {
            throw new RemoteReleaseUpdateException(
                "invalid_station",
                "The remote release-update station identity is invalid.");
        }
        string correlationId = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(16));
        BrokerReleaseUpdateMessage message = new(
            StationMessageTypes.ReleaseUpdate,
            correlationId,
            request.ReleaseIdentity);
        string? validation = StationProtocolValidator.ValidateReleaseUpdate(message);
        if (validation is not null)
        {
            throw new RemoteReleaseUpdateException("invalid_request", validation);
        }

        StationLink link;
        PendingRequest pending;
        lock (m_gate)
        {
            if (!m_links.TryGetValue(request.StationId, out StationLink? found))
            {
                throw new RemoteReleaseUpdateException(
                    "station_offline",
                    "The remote station is not connected.");
            }
            link = found;
            if (!link.Capabilities.Contains(
                    StationCapabilities.ReleaseUpdateV1,
                    StringComparer.Ordinal))
            {
                throw new RemoteReleaseUpdateException(
                    "station_capability",
                    "The remote station does not grant signed release updates.");
            }
            if (m_pending.Values.Any(item => string.Equals(
                    item.StationId,
                    request.StationId,
                    StringComparison.Ordinal)))
            {
                throw new RemoteReleaseUpdateException(
                    "station_busy",
                    "A signed release update is already pending for this station.");
            }
            pending = new PendingRequest(
                request.StationId,
                message);
            m_pending.Add(correlationId, pending);
            TrackRecentRequestLocked(request.StationId, message);
        }

        try
        {
            await link.Sender(message, cancellationToken);
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            return await pending.Completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteReleaseUpdateException(
                "station_timeout",
                "The remote station did not complete the signed release update in time.");
        }
        finally
        {
            lock (m_gate)
            {
                m_pending.Remove(correlationId);
            }
        }
    }

    public bool HandleStationMessage(
        string stationId,
        string connectionId,
        JsonElement root) =>
        TryHandleStationMessage(
            stationId,
            connectionId,
            root,
            out _);

    public bool TryHandleStationMessage(
        string stationId,
        string connectionId,
        JsonElement root,
        out BrokerReleaseUpdateAcknowledgementMessage? acknowledgement)
    {
        acknowledgement = null;
        try
        {
            StationReleaseUpdateResultMessage? message =
                root.Deserialize<StationReleaseUpdateResultMessage>(
                    StationProtocol.JsonOptions);
            if (message is null ||
                StationProtocolValidator.ValidateReleaseUpdateResult(message)
                    is not null)
            {
                return false;
            }

            PendingRequest? pending;
            lock (m_gate)
            {
                PruneRecentRequestsLocked(DateTimeOffset.UtcNow);
                if (!m_recent.TryGetValue(
                        message.CorrelationId,
                        out RecentRequest? recent) ||
                    !string.Equals(
                        recent.StationId,
                        stationId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        recent.ReleaseIdentity,
                        message.ReleaseIdentity,
                        StringComparison.Ordinal) ||
                    recent.Result is not null &&
                    !Equals(recent.Result, message))
                {
                    return false;
                }

                if (m_pending.TryGetValue(
                        message.CorrelationId,
                        out pending) &&
                    (!string.Equals(
                        pending.StationId,
                        stationId,
                        StringComparison.Ordinal) ||
                     !string.Equals(
                        pending.Message.ReleaseIdentity,
                        message.ReleaseIdentity,
                        StringComparison.Ordinal)))
                {
                    return false;
                }

                m_recent[message.CorrelationId] = recent with
                {
                    Result = message,
                    ExpiresAt = DateTimeOffset.UtcNow.Add(
                        RecentRequestLifetime)
                };
            }

            pending?.Completion.TrySetResult(
                new RemoteReleaseUpdateResult(
                    stationId,
                    message.CorrelationId,
                    message.ReleaseIdentity,
                    message.Succeeded,
                    message.Outcome,
                    message.ActiveReleaseIdentity,
                    message.RolledBack));
            acknowledgement = new BrokerReleaseUpdateAcknowledgementMessage(
                StationMessageTypes.ReleaseUpdateAcknowledgement,
                message.CorrelationId,
                message.ReleaseIdentity);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void TrackRecentRequestLocked(
        string stationId,
        BrokerReleaseUpdateMessage message)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PruneRecentRequestsLocked(now);
        if (m_recent.Count >= MaximumRecentRequests)
        {
            KeyValuePair<string, RecentRequest> oldest = m_recent
                .OrderBy(item => item.Value.ExpiresAt)
                .First();
            m_recent.Remove(oldest.Key);
        }
        if (!m_recent.TryAdd(
                message.CorrelationId,
                new RecentRequest(
                    stationId,
                    message.ReleaseIdentity,
                    now.Add(RecentRequestLifetime),
                    null)))
        {
            throw new InvalidOperationException(
                "A remote release-update correlation ID was reused.");
        }
    }

    private void PruneRecentRequestsLocked(DateTimeOffset now)
    {
        foreach (string correlationId in m_recent
                     .Where(item => item.Value.ExpiresAt <= now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            m_recent.Remove(correlationId);
        }
    }

    private void Detach(string stationId, string connectionId)
    {
        lock (m_gate)
        {
            if (m_links.TryGetValue(stationId, out StationLink? link) &&
                string.Equals(link.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                m_links.Remove(stationId);
            }
        }
    }

    private sealed record RecentRequest(
        string StationId,
        string ReleaseIdentity,
        DateTimeOffset ExpiresAt,
        StationReleaseUpdateResultMessage? Result);

    private sealed record StationLink(
        string ConnectionId,
        IReadOnlyList<string> Capabilities,
        Func<object, CancellationToken, Task> Sender);

    private sealed class PendingRequest(
        string stationId,
        BrokerReleaseUpdateMessage message)
    {
        internal string StationId { get; } = stationId;
        internal BrokerReleaseUpdateMessage Message { get; } = message;
        internal TaskCompletionSource<RemoteReleaseUpdateResult> Completion
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class StationUpdateLease(
        RemoteReleaseUpdateBroker owner,
        string stationId,
        string connectionId) : IDisposable
    {
        private int m_disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_disposed, 1) == 0)
            {
                owner.Detach(stationId, connectionId);
            }
        }
    }
}
