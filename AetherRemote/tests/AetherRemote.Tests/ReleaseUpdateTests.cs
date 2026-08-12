using System.Text.Json;
using AetherRemote.Agent;
using AetherRemote.Broker;
using AetherRemote.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherRemote.Tests;

public sealed class ReleaseUpdateTests
{
    [Fact]
    public void ProtocolAcceptsOnlyExactReleaseIdentityUpdateRequests()
    {
        BrokerReleaseUpdateMessage valid = new(
            StationMessageTypes.ReleaseUpdate,
            "0123456789abcdef0123456789abcdef",
            "aethersdr-8.5.0-beta.1");

        Assert.Null(StationProtocolValidator.ValidateReleaseUpdate(valid));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseUpdate(
                valid with { ReleaseIdentity = "../release" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseUpdate(
                valid with { CorrelationId = "not-a-correlation" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseUpdate(
                valid with { Type = "broker.command" }));

        BrokerReleaseUpdateAcknowledgementMessage acknowledgement = new(
            StationMessageTypes.ReleaseUpdateAcknowledgement,
            valid.CorrelationId,
            valid.ReleaseIdentity);
        Assert.Null(
            StationProtocolValidator.ValidateReleaseUpdateAcknowledgement(
                acknowledgement));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseUpdateAcknowledgement(
                acknowledgement with { Type = "broker.command" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateReleaseUpdateAcknowledgement(
                acknowledgement with { ReleaseIdentity = "../release" }));
    }

    [Fact]
    public void ProtocolRejectsArbitraryLocalUpdaterActions()
    {
        LocalStationReleaseUpdateRequest valid = new(
            StationLocalUpdaterMessageTypes.Request,
            "0123456789abcdef0123456789abcdef",
            "aethersdr-8.5.0-beta.1",
            StationLocalUpdaterActions.Apply);

        Assert.Null(
            StationProtocolValidator.ValidateLocalReleaseUpdateRequest(valid));
        Assert.NotNull(
            StationProtocolValidator.ValidateLocalReleaseUpdateRequest(
                valid with { Action = "exec" }));
        Assert.NotNull(
            StationProtocolValidator.ValidateLocalReleaseUpdateRequest(
                valid with { ReleaseIdentity = "/tmp/payload" }));
    }

    [Fact]
    public void ReleaseUpdateCapabilityRequiresExactHelloReleaseMetadata()
    {
        StationHelloMessage legacy = new(
            StationMessageTypes.Hello,
            StationProtocol.Version,
            "station-one",
            "0123456789abcdef0123456789abcdef",
            "0.3.6",
            [StationCapabilities.ReceiveProjectionV1]);
        Assert.Null(
            StationProtocolValidator.ValidateHello(legacy, "station-one"));

        StationHelloMessage missingMetadata = legacy with
        {
            Capabilities = [StationCapabilities.ReleaseUpdateV1]
        };
        Assert.NotNull(
            StationProtocolValidator.ValidateHello(
                missingMetadata,
                "station-one"));

        StationHelloMessage updateCapable = missingMetadata with
        {
            ReleaseIdentity = "aethersdr-8.5.0-beta.1",
            StationEngineVersion = "8.5.0"
        };
        Assert.Null(
            StationProtocolValidator.ValidateHello(
                updateCapable,
                "station-one"));
    }

    [Fact]
    public async Task BrokerRequiresExplicitReleaseUpdateCapability()
    {
        RemoteReleaseUpdateBroker broker = new();
        using RemoteReleaseUpdateBroker.StationUpdateLease lease =
            broker.AttachStation(
                "station-one",
                "0123456789abcdef0123456789abcdef",
                [StationCapabilities.ReceiveProjectionV1],
                (_, _) => Task.CompletedTask);

        RemoteReleaseUpdateException exception =
            await Assert.ThrowsAsync<RemoteReleaseUpdateException>(() =>
                broker.ExecuteAsync(
                    new RemoteReleaseUpdateRequest(
                        "station-one",
                        "aethersdr-8.5.0-beta.1"),
                    CancellationToken.None));

        Assert.Equal("station_capability", exception.Code);
    }

    [Fact]
    public async Task BrokerCorrelatesAcrossExpectedStationRestart()
    {
        RemoteReleaseUpdateBroker broker = new();
        TaskCompletionSource<BrokerReleaseUpdateMessage> sent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        const string firstConnectionId =
            "0123456789abcdef0123456789abcdef";
        RemoteReleaseUpdateBroker.StationUpdateLease firstLease =
            broker.AttachStation(
                "station-one",
                firstConnectionId,
                [StationCapabilities.ReleaseUpdateV1],
                (message, _) =>
                {
                    sent.TrySetResult(
                        Assert.IsType<BrokerReleaseUpdateMessage>(message));
                    return Task.CompletedTask;
                });

        Task<RemoteReleaseUpdateResult> pending = broker.ExecuteAsync(
            new RemoteReleaseUpdateRequest(
                "station-one",
                "aethersdr-8.5.0-beta.1"),
            CancellationToken.None);
        BrokerReleaseUpdateMessage request = await sent.Task;

        Assert.False(
            broker.HandleStationMessage(
                "station-one",
                firstConnectionId,
                JsonSerializer.SerializeToElement(
                    new StationReleaseUpdateResultMessage(
                        StationMessageTypes.ReleaseUpdateResult,
                        request.CorrelationId,
                        "aethersdr-8.5.0-beta.2",
                        true,
                        "applied",
                        "aethersdr-8.5.0-beta.2",
                        false),
                    StationProtocol.JsonOptions)));

        firstLease.Dispose();
        const string restartedConnectionId =
            "fedcba9876543210fedcba9876543210";
        using RemoteReleaseUpdateBroker.StationUpdateLease restartedLease =
            broker.AttachStation(
                "station-one",
                restartedConnectionId,
                [StationCapabilities.ReleaseUpdateV1],
                (_, _) => Task.CompletedTask);

        StationReleaseUpdateResultMessage response = new(
            StationMessageTypes.ReleaseUpdateResult,
            request.CorrelationId,
            request.ReleaseIdentity,
            true,
            "confirmed",
            request.ReleaseIdentity,
            false);
        Assert.True(
            broker.TryHandleStationMessage(
                "station-one",
                restartedConnectionId,
                JsonSerializer.SerializeToElement(
                    response,
                    StationProtocol.JsonOptions),
                out BrokerReleaseUpdateAcknowledgementMessage?
                    acknowledgement));
        Assert.NotNull(acknowledgement);
        Assert.Equal(request.CorrelationId, acknowledgement.CorrelationId);
        Assert.Equal(request.ReleaseIdentity, acknowledgement.ReleaseIdentity);

        RemoteReleaseUpdateResult result = await pending;
        Assert.True(result.Succeeded);
        Assert.Equal(request.ReleaseIdentity, result.ActiveReleaseIdentity);
        Assert.False(result.RolledBack);

        restartedLease.Dispose();
        const string secondRestartConnectionId =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using RemoteReleaseUpdateBroker.StationUpdateLease secondRestartLease =
            broker.AttachStation(
                "station-one",
                secondRestartConnectionId,
                [StationCapabilities.ReleaseUpdateV1],
                (_, _) => Task.CompletedTask);
        Assert.True(
            broker.TryHandleStationMessage(
                "station-one",
                secondRestartConnectionId,
                JsonSerializer.SerializeToElement(
                    response,
                    StationProtocol.JsonOptions),
                out BrokerReleaseUpdateAcknowledgementMessage?
                    duplicateAcknowledgement));
        Assert.Equal(acknowledgement, duplicateAcknowledgement);
        Assert.False(
            broker.HandleStationMessage(
                "station-one",
                secondRestartConnectionId,
                JsonSerializer.SerializeToElement(
                    response with { Outcome = "changed-result" },
                    StationProtocol.JsonOptions)));
    }

    [Fact]
    public async Task BrokerAcknowledgesTrackedCompletionAfterCallerCancellation()
    {
        RemoteReleaseUpdateBroker broker = new();
        TaskCompletionSource<BrokerReleaseUpdateMessage> sent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        using RemoteReleaseUpdateBroker.StationUpdateLease lease =
            broker.AttachStation(
                "station-one",
                "0123456789abcdef0123456789abcdef",
                [StationCapabilities.ReleaseUpdateV1],
                (message, _) =>
                {
                    sent.TrySetResult(
                        Assert.IsType<BrokerReleaseUpdateMessage>(message));
                    return Task.CompletedTask;
                });
        using CancellationTokenSource cancellation = new();
        Task<RemoteReleaseUpdateResult> pending = broker.ExecuteAsync(
            new RemoteReleaseUpdateRequest(
                "station-one",
                "aethersdr-8.5.0-beta.1"),
            cancellation.Token);
        BrokerReleaseUpdateMessage request = await sent.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pending);

        StationReleaseUpdateResultMessage response = new(
            StationMessageTypes.ReleaseUpdateResult,
            request.CorrelationId,
            request.ReleaseIdentity,
            false,
            "startup-rollback",
            "aethersdr-8.4.0-beta.6",
            true);
        Assert.True(
            broker.TryHandleStationMessage(
                "station-one",
                "0123456789abcdef0123456789abcdef",
                JsonSerializer.SerializeToElement(
                    response,
                    StationProtocol.JsonOptions),
                out BrokerReleaseUpdateAcknowledgementMessage?
                    acknowledgement));
        Assert.Equal(request.CorrelationId, acknowledgement?.CorrelationId);
    }

    [Fact]
    public void BrokerRejectsUntrackedLateCompletion()
    {
        RemoteReleaseUpdateBroker broker = new();
        StationReleaseUpdateResultMessage response = new(
            StationMessageTypes.ReleaseUpdateResult,
            "0123456789abcdef0123456789abcdef",
            "aethersdr-8.5.0-beta.1",
            false,
            "startup-rollback",
            "aethersdr-8.4.0-beta.6",
            true);

        Assert.False(
            broker.HandleStationMessage(
                "station-one",
                "fedcba9876543210fedcba9876543210",
                JsonSerializer.SerializeToElement(
                    response,
                    StationProtocol.JsonOptions)));
    }

    [Fact]
    public async Task AgentAcknowledgesExactSuccessfulStartupCompletion()
    {
        LocalStationReleaseUpdateRequest? observed = null;
        StationReleaseUpdateService service = new(
            Options.Create(new AgentSettings
            {
                ReleaseUpdateEnabled = true,
                ReleaseIdentity = "aethersdr-8.5.0-beta.1"
            }),
            new RecordingLocalUpdaterClient((request, _) =>
            {
                observed = request;
                return Task.FromResult(
                    new LocalStationReleaseUpdateResult(
                        StationLocalUpdaterMessageTypes.Result,
                        request.CorrelationId,
                        request.ReleaseIdentity,
                        request.Action,
                        true,
                        "acknowledged",
                        request.ReleaseIdentity,
                        request.ReleaseIdentity,
                        false,
                        "aethersdr-8.5.0-beta.1",
                        false));
            }),
            NullLogger<StationReleaseUpdateService>.Instance);
        StationReleaseUpdateResultMessage completion = new(
            StationMessageTypes.ReleaseUpdateResult,
            "0123456789abcdef0123456789abcdef",
            "aethersdr-8.5.0-beta.1",
            true,
            "confirmed",
            "aethersdr-8.5.0-beta.1",
            false);
        BrokerReleaseUpdateAcknowledgementMessage acknowledgement = new(
            StationMessageTypes.ReleaseUpdateAcknowledgement,
            completion.CorrelationId,
            completion.ReleaseIdentity);

        await service.AcknowledgeStartupAsync(
            completion,
            acknowledgement,
            CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(StationLocalUpdaterActions.Acknowledge, observed.Action);
        Assert.Equal(completion.CorrelationId, observed.CorrelationId);
        Assert.Equal("aethersdr-8.5.0-beta.1", observed.ReleaseIdentity);
    }

    [Fact]
    public async Task AgentRollbackAcknowledgementUsesCurrentActiveRelease()
    {
        LocalStationReleaseUpdateRequest? observed = null;
        StationReleaseUpdateService service = new(
            Options.Create(new AgentSettings
            {
                ReleaseUpdateEnabled = true,
                ReleaseIdentity = "aethersdr-8.4.0-beta.6"
            }),
            new RecordingLocalUpdaterClient((request, _) =>
            {
                observed = request;
                return Task.FromResult(
                    new LocalStationReleaseUpdateResult(
                        StationLocalUpdaterMessageTypes.Result,
                        request.CorrelationId,
                        request.ReleaseIdentity,
                        request.Action,
                        true,
                        "acknowledged",
                        request.ReleaseIdentity,
                        request.ReleaseIdentity,
                        false,
                        "aethersdr-8.5.0-beta.1",
                        true));
            }),
            NullLogger<StationReleaseUpdateService>.Instance);
        StationReleaseUpdateResultMessage completion = new(
            StationMessageTypes.ReleaseUpdateResult,
            "0123456789abcdef0123456789abcdef",
            "aethersdr-8.5.0-beta.1",
            false,
            "startup-rollback",
            "aethersdr-8.4.0-beta.6",
            true);
        BrokerReleaseUpdateAcknowledgementMessage acknowledgement = new(
            StationMessageTypes.ReleaseUpdateAcknowledgement,
            completion.CorrelationId,
            completion.ReleaseIdentity);

        await service.AcknowledgeStartupAsync(
            completion,
            acknowledgement,
            CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal("aethersdr-8.4.0-beta.6", observed.ReleaseIdentity);
        Assert.Equal(completion.CorrelationId, observed.CorrelationId);
    }

    [Fact]
    public async Task AgentRejectsMismatchedBrokerAcknowledgementBeforeLocalMutation()
    {
        int calls = 0;
        StationReleaseUpdateService service = new(
            Options.Create(new AgentSettings
            {
                ReleaseUpdateEnabled = true,
                ReleaseIdentity = "aethersdr-8.5.0-beta.1"
            }),
            new RecordingLocalUpdaterClient((_, _) =>
            {
                calls++;
                throw new InvalidOperationException("should not be called");
            }),
            NullLogger<StationReleaseUpdateService>.Instance);
        StationReleaseUpdateResultMessage completion = new(
            StationMessageTypes.ReleaseUpdateResult,
            "0123456789abcdef0123456789abcdef",
            "aethersdr-8.5.0-beta.1",
            true,
            "confirmed",
            "aethersdr-8.5.0-beta.1",
            false);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.AcknowledgeStartupAsync(
                completion,
                new BrokerReleaseUpdateAcknowledgementMessage(
                    StationMessageTypes.ReleaseUpdateAcknowledgement,
                    "fedcba9876543210fedcba9876543210",
                    completion.ReleaseIdentity),
                CancellationToken.None));
        Assert.Equal(0, calls);
    }

    private sealed class RecordingLocalUpdaterClient(
        Func<
            LocalStationReleaseUpdateRequest,
            CancellationToken,
            Task<LocalStationReleaseUpdateResult>> execute) :
        IStationReleaseUpdateLocalClient
    {
        public Task<LocalStationReleaseUpdateResult> ExecuteAsync(
            LocalStationReleaseUpdateRequest request,
            CancellationToken cancellationToken) =>
            execute(request, cancellationToken);
    }
}
