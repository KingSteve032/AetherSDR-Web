using System.Security.Cryptography;
using System.Text.Json;
using AetherRemote.Protocol;

namespace AetherRemote.Broker;

public sealed record RemoteReleaseServiceControlRequest(
    string StationId,
    string ReleaseIdentity,
    string Phase,
    string Action,
    string ServiceRole,
    string UnitIdentity);

public sealed record RemoteReleaseServiceControlResult(
    string StationId,
    string CorrelationId,
    string ReleaseIdentity,
    string Phase,
    string Action,
    string ServiceRole,
    string UnitIdentity,
    bool Succeeded,
    string Outcome);

public sealed class RemoteReleaseServiceControlException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Correlates one exact fixed-purpose release service-control request with one
/// connected station. It has no arbitrary command or payload field and accepts
/// results only from the same station connection that received the request.
/// </summary>
public sealed class RemoteReleaseServiceControlBroker
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly object m_gate = new();
    private readonly Dictionary<string, StationLink> m_links =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingRequest> m_pending =
        new(StringComparer.Ordinal);

    public StationControlLease AttachStation(
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
        return new StationControlLease(this, stationId, connectionId);
    }

    public async Task<RemoteReleaseServiceControlResult> ExecuteAsync(
        RemoteReleaseServiceControlRequest request,
        CancellationToken cancellationToken)
    {
        if (!StationProtocolValidator.IsIdentifier(
                request.StationId,
                StationProtocol.MaximumStationIdLength))
        {
            throw new RemoteReleaseServiceControlException(
                "invalid_station",
                "The remote release-control station identity is invalid.");
        }
        string correlationId = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(16));
        BrokerReleaseServiceControlMessage message = new(
            StationMessageTypes.ReleaseServiceControl,
            correlationId,
            request.ReleaseIdentity,
            request.Phase,
            request.Action,
            request.ServiceRole,
            request.UnitIdentity);
        string? validation =
            StationProtocolValidator.ValidateReleaseServiceControl(message);
        if (validation is not null)
        {
            throw new RemoteReleaseServiceControlException(
                "invalid_request",
                validation);
        }

        StationLink link;
        PendingRequest pending;
        lock (m_gate)
        {
            if (!m_links.TryGetValue(request.StationId, out StationLink? found))
            {
                throw new RemoteReleaseServiceControlException(
                    "station_offline",
                    "The remote station is not connected.");
            }
            link = found;
            if (!link.Capabilities.Contains(
                    StationCapabilities.ReleaseServiceControlV1,
                    StringComparer.Ordinal))
            {
                throw new RemoteReleaseServiceControlException(
                    "station_capability",
                    "The remote station does not grant release service control.");
            }
            pending = new PendingRequest(
                request.StationId,
                link.ConnectionId,
                message);
            m_pending.Add(correlationId, pending);
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
            throw new RemoteReleaseServiceControlException(
                "station_timeout",
                "The remote station did not return a release service-control result in time.");
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
        JsonElement root)
    {
        try
        {
            StationReleaseServiceControlResultMessage? message =
                root.Deserialize<StationReleaseServiceControlResultMessage>(
                    StationProtocol.JsonOptions);
            if (StationProtocolValidator.ValidateReleaseServiceControlResult(message)
                    is not null ||
                message is null)
            {
                return false;
            }

            PendingRequest? pending;
            lock (m_gate)
            {
                if (!m_pending.TryGetValue(
                        message.CorrelationId,
                        out pending) ||
                    !string.Equals(
                        pending.StationId,
                        stationId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        pending.ConnectionId,
                        connectionId,
                        StringComparison.Ordinal) ||
                    !Matches(pending.Message, message))
                {
                    return false;
                }
            }

            pending.Completion.TrySetResult(
                new RemoteReleaseServiceControlResult(
                    stationId,
                    message.CorrelationId,
                    message.ReleaseIdentity,
                    message.Phase,
                    message.Action,
                    message.ServiceRole,
                    message.UnitIdentity,
                    message.Succeeded,
                    message.Outcome));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void Detach(string stationId, string connectionId)
    {
        PendingRequest[] abandoned;
        lock (m_gate)
        {
            if (m_links.TryGetValue(stationId, out StationLink? link) &&
                string.Equals(link.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                m_links.Remove(stationId);
            }
            abandoned = m_pending.Values
                .Where(item =>
                    string.Equals(item.StationId, stationId, StringComparison.Ordinal) &&
                    string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))
                .ToArray();
        }
        foreach (PendingRequest request in abandoned)
        {
            request.Completion.TrySetException(
                new RemoteReleaseServiceControlException(
                    "station_disconnected",
                    "The remote station disconnected during release service control."));
        }
    }

    private static bool Matches(
        BrokerReleaseServiceControlMessage request,
        StationReleaseServiceControlResultMessage result) =>
        string.Equals(request.ReleaseIdentity, result.ReleaseIdentity, StringComparison.Ordinal) &&
        string.Equals(request.Phase, result.Phase, StringComparison.Ordinal) &&
        string.Equals(request.Action, result.Action, StringComparison.Ordinal) &&
        string.Equals(request.ServiceRole, result.ServiceRole, StringComparison.Ordinal) &&
        string.Equals(request.UnitIdentity, result.UnitIdentity, StringComparison.Ordinal);

    private sealed record StationLink(
        string ConnectionId,
        IReadOnlyList<string> Capabilities,
        Func<object, CancellationToken, Task> Sender);

    private sealed class PendingRequest(
        string stationId,
        string connectionId,
        BrokerReleaseServiceControlMessage message)
    {
        internal string StationId { get; } = stationId;
        internal string ConnectionId { get; } = connectionId;
        internal BrokerReleaseServiceControlMessage Message { get; } = message;
        internal TaskCompletionSource<RemoteReleaseServiceControlResult> Completion
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class StationControlLease(
        RemoteReleaseServiceControlBroker owner,
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
