using System.Text.Json;
using AetherRemote.Agent;
using AetherRemote.Broker;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Tests;

public sealed class ReleaseServiceControlTests
{
    [Fact]
    public async Task AgentReleaseControlIsDisabledBeforeUpdaterClient()
    {
        int calls = 0;
        StationReleaseServiceControlService service = new(
            Options.Create(
                new AgentSettings
                {
                    ReleaseServiceControlEnabled = false,
                    Capabilities =
                    [
                        StationCapabilities.ReceiveProjectionV1,
                        StationCapabilities.ReleaseServiceControlV1
                    ]
                }),
            new StubUpdaterClient((request, cancellationToken) =>
            {
                calls++;
                return Task.FromResult(Result(request, true, "completed"));
            }));

        StationReleaseServiceControlResultMessage result =
            await service.ExecuteAsync(Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("execution-disabled", result.Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AgentForwardsOnlyValidatedFixedRequestToUpdaterClient()
    {
        BrokerReleaseServiceControlMessage? observed = null;
        StationReleaseServiceControlService service = new(
            Options.Create(
                new AgentSettings
                {
                    ReleaseServiceControlEnabled = true,
                    Capabilities =
                    [
                        StationCapabilities.ReceiveProjectionV1,
                        StationCapabilities.ReleaseServiceControlV1
                    ]
                }),
            new StubUpdaterClient((request, cancellationToken) =>
            {
                observed = request;
                return Task.FromResult(Result(request, true, "completed"));
            }));

        StationReleaseServiceControlResultMessage result =
            await service.ExecuteAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("completed", result.Outcome);
        Assert.Equal(Request(), observed);
    }

    [Fact]
    public async Task AgentRejectsInvalidUnitBeforeUpdaterClient()
    {
        int calls = 0;
        StationReleaseServiceControlService service = new(
            Options.Create(
                new AgentSettings
                {
                    ReleaseServiceControlEnabled = true,
                    Capabilities = [StationCapabilities.ReleaseServiceControlV1]
                }),
            new StubUpdaterClient((request, cancellationToken) =>
            {
                calls++;
                return Task.FromResult(Result(request, true, "completed"));
            }));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ExecuteAsync(
                Request() with { UnitIdentity = "evil.service" },
                CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BrokerCorrelatesResultToExactStationConnectionAndRequest()
    {
        RemoteReleaseServiceControlBroker broker = new();
        TaskCompletionSource<BrokerReleaseServiceControlMessage> sent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        using RemoteReleaseServiceControlBroker.StationControlLease lease =
            broker.AttachStation(
                "station-one",
                "connection-one",
                [StationCapabilities.ReleaseServiceControlV1],
                (message, cancellationToken) =>
                {
                    sent.SetResult(
                        Assert.IsType<BrokerReleaseServiceControlMessage>(message));
                    return Task.CompletedTask;
                });

        Task<RemoteReleaseServiceControlResult> pending =
            broker.ExecuteAsync(
                new RemoteReleaseServiceControlRequest(
                    "station-one",
                    "aethersdr-8.3.0",
                    "pre-switch-stop",
                    "stop",
                    "station-engine",
                    "aetherremote-station-engine.service"),
                CancellationToken.None);
        BrokerReleaseServiceControlMessage request = await sent.Task;
        StationReleaseServiceControlResultMessage response = new(
            StationMessageTypes.ReleaseServiceControlResult,
            request.CorrelationId,
            request.ReleaseIdentity,
            request.Phase,
            request.Action,
            request.ServiceRole,
            request.UnitIdentity,
            Succeeded: true,
            Outcome: "completed");
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(response, StationProtocol.JsonOptions));

        Assert.True(
            broker.HandleStationMessage(
                "station-one",
                "connection-one",
                document.RootElement));
        RemoteReleaseServiceControlResult result = await pending;
        Assert.True(result.Succeeded);
        Assert.Equal(request.CorrelationId, result.CorrelationId);
        Assert.Equal("station-one", result.StationId);
    }

    [Fact]
    public async Task BrokerRejectsStationWithoutFixedReleaseCapability()
    {
        RemoteReleaseServiceControlBroker broker = new();
        using RemoteReleaseServiceControlBroker.StationControlLease lease =
            broker.AttachStation(
                "station-one",
                "connection-one",
                [StationCapabilities.ReceiveProjectionV1],
                (message, cancellationToken) => Task.CompletedTask);

        RemoteReleaseServiceControlException exception =
            await Assert.ThrowsAsync<RemoteReleaseServiceControlException>(
                () => broker.ExecuteAsync(
                    new RemoteReleaseServiceControlRequest(
                        "station-one",
                        "aethersdr-8.3.0",
                        "pre-switch-stop",
                        "stop",
                        "station-engine",
                        "aetherremote-station-engine.service"),
                    CancellationToken.None));

        Assert.Equal("station_capability", exception.Code);
    }

    private sealed class StubUpdaterClient(
        Func<
            BrokerReleaseServiceControlMessage,
            CancellationToken,
            Task<StationReleaseServiceControlResultMessage>> execute) :
        IStationReleaseUpdaterClient
    {
        public Task<StationReleaseServiceControlResultMessage> ExecuteAsync(
            BrokerReleaseServiceControlMessage request,
            CancellationToken cancellationToken) =>
            execute(request, cancellationToken);
    }

    private static BrokerReleaseServiceControlMessage Request() =>
        new(
            StationMessageTypes.ReleaseServiceControl,
            "0123456789abcdef0123456789abcdef",
            "aethersdr-8.3.0",
            "pre-switch-stop",
            "stop",
            "aetherremote-agent",
            "aetherremote-agent.service");

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
