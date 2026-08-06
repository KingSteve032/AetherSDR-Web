using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Agent;

/// <summary>
/// Disabled-by-default client boundary for the separate owner-only AetherRemote
/// updater daemon. The agent validates and forwards only exact fixed-unit
/// release service-control messages; it never executes systemctl, a shell,
/// arbitrary arguments, radio commands, or TX operations itself.
/// </summary>
public sealed class StationReleaseServiceControlService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);
    private readonly AgentSettings m_settings;
    private readonly IStationReleaseUpdaterClient m_client;

    public StationReleaseServiceControlService(
        IOptions<AgentSettings> settings,
        IStationReleaseUpdaterClient client)
    {
        m_settings = settings?.Value ??
            throw new ArgumentNullException(nameof(settings));
        m_client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<StationReleaseServiceControlResultMessage> ExecuteAsync(
        BrokerReleaseServiceControlMessage request,
        CancellationToken cancellationToken)
    {
        string? validation =
            StationProtocolValidator.ValidateReleaseServiceControl(request);
        if (validation is not null)
        {
            throw new InvalidDataException(validation);
        }
        if (!m_settings.ReleaseServiceControlEnabled ||
            !(m_settings.Capabilities ?? []).Contains(
                StationCapabilities.ReleaseServiceControlV1,
                StringComparer.Ordinal))
        {
            return Result(request, false, "execution-disabled");
        }
        if (!OperatingSystem.IsLinux())
        {
            return Result(request, false, "unsupported-platform");
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            StationReleaseServiceControlResultMessage result =
                await m_client.ExecuteAsync(request, timeout.Token);
            return StationProtocolValidator.ValidateReleaseServiceControlResult(result)
                    is null &&
                Matches(request, result)
                ? result
                : Result(request, false, "updater-response-invalid");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return Result(request, false, "updater-timeout");
        }
        catch (Exception exception)
            when (exception is IOException or InvalidDataException or
                InvalidOperationException or NotSupportedException or
                System.Net.Sockets.SocketException or System.Text.Json.JsonException)
        {
            return Result(request, false, "updater-unavailable");
        }
    }

    private static bool Matches(
        BrokerReleaseServiceControlMessage request,
        StationReleaseServiceControlResultMessage result) =>
        string.Equals(request.CorrelationId, result.CorrelationId, StringComparison.Ordinal) &&
        string.Equals(request.ReleaseIdentity, result.ReleaseIdentity, StringComparison.Ordinal) &&
        string.Equals(request.Phase, result.Phase, StringComparison.Ordinal) &&
        string.Equals(request.Action, result.Action, StringComparison.Ordinal) &&
        string.Equals(request.ServiceRole, result.ServiceRole, StringComparison.Ordinal) &&
        string.Equals(request.UnitIdentity, result.UnitIdentity, StringComparison.Ordinal);

    private static StationReleaseServiceControlResultMessage Result(
        BrokerReleaseServiceControlMessage request,
        bool succeeded,
        string outcome) =>
        new(
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
