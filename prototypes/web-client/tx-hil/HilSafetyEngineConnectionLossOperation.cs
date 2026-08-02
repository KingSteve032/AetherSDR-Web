using System.Text.Json;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging;

namespace AetherSDR.TxHil;

internal sealed class HilSafetyEngineConnectionLossOperation(
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan HeartbeatTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(25);

    private readonly ILoggerFactory m_loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly TimeProvider m_timeProvider =
        timeProvider ?? TimeProvider.System;

    public async Task<int> VerifyPreflightAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession observer = NewSession(options.RadioId);
        await observer.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: false,
            cancellationToken);

        await using HilFlexSession engine = NewSession(options.RadioId);
        await engine.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: true,
            cancellationToken);
        HilRadioSnapshot initial = engine.Snapshot();
        VerifyInitialState(initial, options.ExpectedSerial);
        HilTransmitSettings previousTransmitSettings = initial.TransmitSettings!;

        HilOwnedRadioResources? resources = null;
        TxLeaseManager? leases = null;
        TxLease? lease = null;
        StationTxSafetySupervisor? supervisor = null;
        StationTxEngineConnectionMonitor? connectionMonitor = null;
        CountingEmergencyUnkeyTransport? safetyTransport = null;
        DisconnectableTxCommandTransport? engineTransport = null;
        const string engineInstanceId = "tx-hil-engine-loss-preflight-instance";
        const string sessionId = "tx-hil-engine-loss-preflight";
        const string browserClientId = "tx-hil-engine-loss-preflight-browser";

        try
        {
            resources = await engine.CreateOwnedTxResourcesAsync(
                options,
                cancellationToken);
            HilTransmitSettings capturedSettings =
                await engine.ConfigureSilentTransmitAsync(cancellationToken);
            if (capturedSettings != previousTransmitSettings)
            {
                throw new InvalidOperationException(
                    "The transmit settings changed after the initial engine-loss preflight snapshot.");
            }
            await engine.RequestLocalPttAsync(cancellationToken);
            await WaitForObserverOwnershipAsync(
                observer,
                engine.ClientHandle,
                cancellationToken);

            leases = new TxLeaseManager(m_timeProvider);
            if (!leases.TryAcquire(
                    options.RadioId,
                    sessionId,
                    browserClientId,
                    "tx-hil",
                    "PSOC2 Engine Connection Loss Preflight",
                    TimeSpan.FromSeconds(15),
                    out lease,
                    out string? leaseError))
            {
                throw new InvalidOperationException(
                    leaseError ?? "Could not acquire the engine-loss preflight TX lease.");
            }

            safetyTransport = new CountingEmergencyUnkeyTransport(
                new HilEmergencyUnkeyTransport(observer));
            supervisor = new StationTxSafetySupervisor(
                options.RadioId,
                observer.OccupancyRegistry,
                safetyTransport,
                m_timeProvider);
            StationTxSafetyArm safetyArm = new(
                engineInstanceId,
                lease!.LeaseId,
                sessionId,
                browserClientId,
                engine.ClientHandle,
                HeartbeatTimeout);
            RequireSuccess(
                await supervisor.ArmAsync(safetyArm, cancellationToken),
                "arm the independent engine-loss preflight supervisor");

            engineTransport = new DisconnectableTxCommandTransport(
                resources.Transport,
                engineInstanceId,
                lease.LeaseId);
            connectionMonitor = new StationTxEngineConnectionMonitor(supervisor);
            StationTxEngineConnectionResult connected =
                await connectionMonitor.EvaluateAsync(
                    engineTransport.Observation(),
                    cancellationToken);
            RequireConnectionSuccess(
                connected,
                "observe the exact engine command channel connected");

            engineTransport.InjectConnectionLoss();
            StationTxEngineConnectionResult disconnected =
                await connectionMonitor.EvaluateAsync(
                    engineTransport.Observation(),
                    cancellationToken);
            if (!disconnected.Success ||
                disconnected.Code != "unkeyed" ||
                disconnected.SafetySnapshot.State !=
                    StationTxSafetyState.Disarmed ||
                safetyTransport.CommandCount != 0 ||
                engineTransport.KeyCommands != 0 ||
                engineTransport.UnkeyCommands != 0 ||
                engineTransport.PostLossCommandAttempts != 0)
            {
                throw new InvalidOperationException(
                    "The no-RF engine-loss preflight did not disarm from idle with zero radio commands.");
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                test = "independent-engine-command-channel-loss-no-rf-preflight",
                passed = true,
                radio = options.RadioId,
                serial = initial.Serial,
                frequencyHz = options.FrequencyHz,
                txAntenna = options.TxAntenna,
                rfPower = options.RfPower,
                engineClientHandle = $"0x{engine.ClientHandle:x8}",
                observerClientHandle = $"0x{observer.ClientHandle:x8}",
                observerGuiRegistered = observer.GuiRegistered,
                resources = new
                {
                    resources.PanId,
                    resources.WaterfallId,
                    resources.SliceId,
                    initialMode = options.Mode,
                    identificationMode = "CW"
                },
                engineConnection = new
                {
                    engineInstanceId,
                    exactLease = lease.LeaseId,
                    protectedHandle = $"0x{engine.ClientHandle:x8}",
                    connectedObserved = connected.SawConnected,
                    lossSignaled = disconnected.LossSignaled,
                    commandChannelConnected = engineTransport.IsConnected
                },
                commands = new
                {
                    engineKey = engineTransport.KeyCommands,
                    engineUnkey = engineTransport.UnkeyCommands,
                    postLossEngineAttempts =
                        engineTransport.PostLossCommandAttempts,
                    observerUnkey = safetyTransport.CommandCount,
                    observerKeyCapability = false
                },
                interlock = engine.Snapshot().TxOccupancy.StateName,
                rfEmitted = false
            }, JsonOptions));
            return 0;
        }
        finally
        {
            using CancellationTokenSource cleanup =
                new(TimeSpan.FromSeconds(15));
            bool supervisorIdle = true;
            if (supervisor is not null)
            {
                supervisorIdle = await EmergencySupervisorUnkeyAsync(
                    supervisor,
                    cleanup.Token);
            }
            if (connectionMonitor is not null)
            {
                await connectionMonitor.DisposeAsync();
            }
            if (supervisor is not null)
            {
                await supervisor.DisposeAsync();
            }
            if (leases is not null && lease is not null)
            {
                leases.TryRelease(
                    options.RadioId,
                    lease.LeaseId,
                    sessionId,
                    browserClientId,
                    "engine-loss-preflight-cleanup",
                    out _);
            }

            bool radioIdle = supervisorIdle &&
                await ConfirmRadioIdleAsync(
                    engine,
                    TimeSpan.FromSeconds(5),
                    cleanup.Token);
            if (!radioIdle)
            {
                m_loggerFactory
                    .CreateLogger<HilSafetyEngineConnectionLossOperation>()
                    .LogCritical(
                        "The no-RF engine-connection-loss preflight did not confirm idle; verify PSOC2 before continuing");
            }
            else
            {
                try
                {
                    await engine.RestoreTransmitSettingsAsync(
                        previousTransmitSettings,
                        cleanup.Token);
                }
                catch (Exception exception)
                {
                    m_loggerFactory
                        .CreateLogger<HilSafetyEngineConnectionLossOperation>()
                        .LogError(
                            exception,
                            "Could not restore transmit settings after the no-RF engine-loss preflight");
                }
                if (resources is not null)
                {
                    await engine.RemoveOwnedTxResourcesAsync(
                        resources,
                        cleanup.Token);
                }
            }
        }
    }

    public async Task<int> RunAsync(
        HilOptions commandLine,
        CancellationToken cancellationToken)
    {
        HilArmManifest manifest = await HilArmManifest.ConsumeAsync(
            commandLine.ArmFile,
            commandLine.Token,
            HilArmManifest.SafetyEngineConnectionLossPurpose,
            m_timeProvider,
            cancellationToken);
        HilOptions options = HilArmManifest.ToSafetyEngineConnectionLossOptions(
            manifest,
            commandLine.ArmFile,
            commandLine.Token);

        await using HilFlexSession observer = NewSession(options.RadioId);
        await observer.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: false,
            cancellationToken);

        await using HilFlexSession engine = NewSession(options.RadioId);
        await engine.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: true,
            cancellationToken);
        HilRadioSnapshot initial = engine.Snapshot();
        VerifyInitialState(initial, options.ExpectedSerial);
        HilTransmitSettings previousTransmitSettings = initial.TransmitSettings!;

        HilOwnedRadioResources? resources = null;
        StationTxCommandGate? gate = null;
        TxLeaseManager? leases = null;
        TxLease? lease = null;
        StationTxSafetySupervisor? supervisor = null;
        StationTxEngineConnectionMonitor? connectionMonitor = null;
        DisconnectableTxCommandTransport? engineTransport = null;
        CountingEmergencyUnkeyTransport? safetyTransport = null;
        DateTimeOffset? keyedAt = null;
        DateTimeOffset? engineConnectionLostAt = null;
        DateTimeOffset? safetyUnkeyRequestedAt = null;
        DateTimeOffset? idleAt = null;
        HilCwxIdentificationResult? identification = null;
        StationTxGateResult? engineReconciliation = null;
        const string engineInstanceId = "tx-hil-engine-connection-loss-instance";
        const string sessionId = "tx-hil-engine-connection-loss";
        const string browserClientId = "tx-hil-engine-connection-loss-browser";

        try
        {
            resources = await engine.CreateOwnedTxResourcesAsync(
                options,
                cancellationToken);
            HilTransmitSettings capturedSettings =
                await engine.ConfigureSilentTransmitAsync(cancellationToken);
            if (capturedSettings != previousTransmitSettings)
            {
                throw new InvalidOperationException(
                    "The transmit settings changed after the initial safety snapshot; no TX command was sent.");
            }
            await engine.RequestLocalPttAsync(cancellationToken);
            await WaitForObserverOwnershipAsync(
                observer,
                engine.ClientHandle,
                cancellationToken);

            leases = new TxLeaseManager(m_timeProvider);
            if (!leases.TryAcquire(
                    options.RadioId,
                    sessionId,
                    browserClientId,
                    "tx-hil",
                    "PSOC2 Engine Command Channel Loss",
                    TimeSpan.FromSeconds(15),
                    out lease,
                    out string? leaseError))
            {
                throw new InvalidOperationException(
                    leaseError ?? "Could not acquire the engine-loss HIL TX lease.");
            }

            safetyTransport = new CountingEmergencyUnkeyTransport(
                new HilEmergencyUnkeyTransport(observer));
            supervisor = new StationTxSafetySupervisor(
                options.RadioId,
                observer.OccupancyRegistry,
                safetyTransport,
                m_timeProvider);
            StationTxSafetyArm safetyArm = new(
                engineInstanceId,
                lease!.LeaseId,
                sessionId,
                browserClientId,
                engine.ClientHandle,
                HeartbeatTimeout);
            RequireSuccess(
                await supervisor.ArmAsync(safetyArm, cancellationToken),
                "arm the independent engine-loss supervisor");

            engineTransport = new DisconnectableTxCommandTransport(
                resources.Transport,
                engineInstanceId,
                lease.LeaseId);
            connectionMonitor = new StationTxEngineConnectionMonitor(supervisor);
            RequireConnectionSuccess(
                await connectionMonitor.EvaluateAsync(
                    engineTransport.Observation(),
                    cancellationToken),
                "observe the exact engine command channel connected");

            gate = new StationTxCommandGate(
                allowTransmit: true,
                options.RadioId,
                leases,
                engine.OccupancyRegistry,
                engineTransport,
                m_timeProvider);
            StationTxGateResult key = await gate.RequestKeyAsync(
                lease.LeaseId,
                sessionId,
                browserClientId,
                cancellationToken);
            if (!key.Success && key.Snapshot.State != StationTxGateState.KeyPending)
            {
                throw new InvalidOperationException(
                    $"The key request failed closed: {key.Code}: {key.Message}");
            }
            await WaitForGateStateAsync(
                gate,
                StationTxGateState.Keyed,
                TimeSpan.FromSeconds(3),
                cancellationToken);
            keyedAt = m_timeProvider.GetUtcNow();

            StationTxSafetyResult protectedTx = await WaitForSafetyAsync(
                supervisor,
                result =>
                    result.Code == "protected_tx" &&
                    result.Snapshot.SawProtectedTransmit,
                "independent observer to confirm the exact engine TX handle",
                TimeSpan.FromSeconds(3),
                cancellationToken);
            RequireSuccess(
                protectedTx,
                "confirm exact protected transmit ownership");

            engineTransport.InjectConnectionLoss();
            engineConnectionLostAt = m_timeProvider.GetUtcNow();
            StationTxEngineConnectionResult loss =
                await connectionMonitor.EvaluateAsync(
                    engineTransport.Observation(),
                    cancellationToken);
            if (!loss.Success ||
                loss.SafetySnapshot.State !=
                    StationTxSafetyState.UnkeyPending ||
                safetyTransport.CommandCount != 1)
            {
                throw new InvalidOperationException(
                    $"The connection monitor did not issue the exact engine-loss unkey: {loss.Code}: {loss.Message}");
            }
            safetyUnkeyRequestedAt = m_timeProvider.GetUtcNow();

            StationTxSafetyResult safetyIdle = await WaitForSafetyAsync(
                supervisor,
                result =>
                    result.Snapshot.State == StationTxSafetyState.Disarmed &&
                    result.Code is "unkeyed" or "disarmed",
                "independent observer to confirm radio idle",
                TimeSpan.FromSeconds(5),
                cancellationToken);
            RequireSuccess(safetyIdle, "confirm independent-observer idle");
            await ConfirmRadioIdleOrThrowAsync(
                engine,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            idleAt = m_timeProvider.GetUtcNow();

            engineReconciliation = await gate.EvaluateAsync(
                "injected-engine-command-channel-loss",
                cancellationToken);
            if (engineReconciliation.Success ||
                engineReconciliation.Code != "flex_client_lost" ||
                engineReconciliation.Snapshot.State !=
                    StationTxGateState.Faulted)
            {
                throw new InvalidOperationException(
                    "The engine gate did not fail closed after its command channel was injected unavailable.");
            }

            if (engineTransport.KeyCommands != 1 ||
                engineTransport.UnkeyCommands != 0 ||
                engineTransport.PostLossCommandAttempts != 0 ||
                safetyTransport.CommandCount != 1)
            {
                throw new InvalidOperationException(
                    "The engine-loss command split was not exact: one engine key, zero engine unkeys or post-loss attempts, and one observer unkey are required.");
            }

            await engine.SetOwnedSliceModeAsync(
                resources,
                "CW",
                cancellationToken);
            HilCwxIdentifier cwxIdentifier = new(m_timeProvider);
            identification = await cwxIdentifier.IdentifyAsync(
                engine,
                cancellationToken);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                test = "independent-engine-command-channel-loss-unkey",
                passed = true,
                radio = options.RadioId,
                serial = initial.Serial,
                frequencyHz = options.FrequencyHz,
                txAntenna = options.TxAntenna,
                rfPower = options.RfPower,
                engineClientHandle = $"0x{engine.ClientHandle:x8}",
                observerClientHandle = $"0x{observer.ClientHandle:x8}",
                injectedLoss = new
                {
                    type = "station-engine-tx-command-channel",
                    engineInstanceId,
                    exactLease = lease.LeaseId,
                    protectedHandle = $"0x{engine.ClientHandle:x8}",
                    statusSessionRetainedForEvidenceAndCleanup = true,
                    commandChannelConnected = engineTransport.IsConnected,
                    connectionMonitorSawConnected = true,
                    connectionMonitorLossSignaled = true
                },
                engineCommands = new
                {
                    key = engineTransport.KeyCommands,
                    unkey = engineTransport.UnkeyCommands,
                    postLossAttempts = engineTransport.PostLossCommandAttempts
                },
                independentObserverCommands = new
                {
                    unkey = safetyTransport.CommandCount,
                    keyCapability = false
                },
                timing = new
                {
                    keyedAt,
                    engineConnectionLostAt,
                    safetyUnkeyRequestedAt,
                    idleAt,
                    connectionLossToUnkeyRequestMilliseconds =
                        (safetyUnkeyRequestedAt!.Value -
                         engineConnectionLostAt!.Value).TotalMilliseconds,
                    unkeyRequestToIdleMilliseconds =
                        (idleAt!.Value -
                         safetyUnkeyRequestedAt.Value).TotalMilliseconds,
                    keyedToIdleMilliseconds =
                        (idleAt.Value - keyedAt!.Value).TotalMilliseconds
                },
                engineGateReconciliation = new
                {
                    state = engineReconciliation.Snapshot.State.ToString(),
                    engineReconciliation.Code,
                    engineReconciliation.Snapshot.Reason
                },
                identification = new
                {
                    identification.Callsign,
                    identification.Wpm,
                    identification.StartIndex,
                    identification.EndIndex,
                    identification.DrainedAt,
                    identification.IdleAt,
                    identification.SawExactOwnedTransmit
                }
            }, JsonOptions));
            return 0;
        }
        finally
        {
            using CancellationTokenSource cleanup =
                new(TimeSpan.FromSeconds(20));

            bool supervisorIdle = true;
            if (supervisor is not null)
            {
                supervisorIdle = await EmergencySupervisorUnkeyAsync(
                    supervisor,
                    cleanup.Token);
            }
            if (connectionMonitor is not null)
            {
                await connectionMonitor.DisposeAsync();
            }
            if (supervisor is not null)
            {
                await supervisor.DisposeAsync();
            }

            if (gate is not null)
            {
                if (engineTransport is not null && engineTransport.IsConnected)
                {
                    await EmergencyConnectedGateUnkeyAsync(
                        gate,
                        lease,
                        sessionId,
                        browserClientId,
                        cleanup.Token);
                }
                await gate.DisposeAsync();
            }
            if (leases is not null && lease is not null)
            {
                leases.TryRelease(
                    options.RadioId,
                    lease.LeaseId,
                    sessionId,
                    browserClientId,
                    "hil-engine-connection-loss-cleanup",
                    out _);
            }

            bool radioIdle = supervisorIdle &&
                await ConfirmRadioIdleAsync(
                    engine,
                    TimeSpan.FromSeconds(5),
                    cleanup.Token);
            if (!radioIdle)
            {
                m_loggerFactory
                    .CreateLogger<HilSafetyEngineConnectionLossOperation>()
                    .LogCritical(
                        "PSOC2 did not provide fresh idle confirmation after the engine command-channel-loss test. Keep RF power bounded and use the remote power kill immediately");
            }
            else
            {
                if (keyedAt is not null &&
                    identification is null &&
                    resources is not null)
                {
                    try
                    {
                        HilRadioSnapshot safeId = engine.Snapshot();
                        if (safeId.ExternalGuiClients.Count == 0 &&
                            safeId.TxOccupancy.State == RadioTxOccupancyState.Idle &&
                            safeId.TxOccupancy.HasExclusiveLocalPttAuthority(
                                engine.ClientHandle))
                        {
                            await engine.SetOwnedSliceModeAsync(
                                resources,
                                "CW",
                                cleanup.Token);
                            HilCwxIdentifier identifier = new(m_timeProvider);
                            await identifier.IdentifyAsync(
                                engine,
                                cleanup.Token);
                        }
                    }
                    catch (Exception exception)
                    {
                        m_loggerFactory
                            .CreateLogger<HilSafetyEngineConnectionLossOperation>()
                            .LogCritical(
                                exception,
                                "The engine command-channel-loss test emitted RF but automatic cleanup identification failed; identify KC4CAW manually as soon as possible");
                    }
                }

                try
                {
                    await engine.RestoreTransmitSettingsAsync(
                        previousTransmitSettings,
                        cleanup.Token);
                }
                catch (Exception exception)
                {
                    m_loggerFactory
                        .CreateLogger<HilSafetyEngineConnectionLossOperation>()
                        .LogError(
                            exception,
                            "Could not restore the previous transmit settings {TransmitSettings}",
                            previousTransmitSettings);
                }
                if (resources is not null)
                {
                    await engine.RemoveOwnedTxResourcesAsync(
                        resources,
                        cleanup.Token);
                }
            }
        }
    }

    private async Task WaitForObserverOwnershipAsync(
        HilFlexSession observer,
        uint engineClientHandle,
        CancellationToken cancellationToken)
    {
        await observer.WaitForAsync(
            snapshot =>
                snapshot.TxOccupancy.State == RadioTxOccupancyState.Idle &&
                snapshot.TxOccupancy.FreshUntil > m_timeProvider.GetUtcNow() &&
                snapshot.TxOccupancy.LocalPttOwners.Count == 1 &&
                snapshot.TxOccupancy.LocalPttOwners[0].ClientHandle ==
                    engineClientHandle &&
                snapshot.GuiClients.Any(client =>
                    client.ClientHandle == engineClientHandle &&
                    client.LocalPtt),
            TimeSpan.FromSeconds(5),
            cancellationToken);
    }

    private async Task<bool> EmergencySupervisorUnkeyAsync(
        StationTxSafetySupervisor supervisor,
        CancellationToken cancellationToken)
    {
        try
        {
            StationTxSafetyResult abort = await supervisor.AbortAsync(
                "hil-cleanup",
                cancellationToken);
            if (abort.Snapshot.State == StationTxSafetyState.Disarmed)
            {
                return true;
            }
            StationTxSafetyResult idle = await WaitForSafetyAsync(
                supervisor,
                result => result.Snapshot.State == StationTxSafetyState.Disarmed,
                "safety supervisor cleanup idle",
                TimeSpan.FromSeconds(5),
                cancellationToken);
            return idle.Success;
        }
        catch (Exception exception)
        {
            m_loggerFactory
                .CreateLogger<HilSafetyEngineConnectionLossOperation>()
                .LogCritical(
                    exception,
                    "The independent safety observer could not confirm emergency unkey during engine-loss cleanup");
            return false;
        }
    }

    private async Task EmergencyConnectedGateUnkeyAsync(
        StationTxCommandGate gate,
        TxLease? lease,
        string sessionId,
        string browserClientId,
        CancellationToken cancellationToken)
    {
        if (!gate.Snapshot.HasActiveIntent || lease is null)
        {
            return;
        }
        try
        {
            await gate.RequestUnkeyAsync(
                lease.LeaseId,
                sessionId,
                browserClientId,
                cancellationToken);
            await WaitForGateStateAsync(
                gate,
                StationTxGateState.Idle,
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }
        catch (Exception exception)
        {
            m_loggerFactory
                .CreateLogger<HilSafetyEngineConnectionLossOperation>()
                .LogCritical(
                    exception,
                    "The still-connected engine gate could not confirm cleanup unkey before connection-loss injection");
        }
    }

    private async Task ConfirmRadioIdleOrThrowAsync(
        HilFlexSession session,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await session.WaitForAsync(
            snapshot =>
                snapshot.TxOccupancy.State == RadioTxOccupancyState.Idle &&
                snapshot.TxOccupancy.FreshUntil > m_timeProvider.GetUtcNow(),
            timeout,
            cancellationToken);
    }

    private async Task<bool> ConfirmRadioIdleAsync(
        HilFlexSession session,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await ConfirmRadioIdleOrThrowAsync(
                session,
                timeout,
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<StationTxSafetyResult> WaitForSafetyAsync(
        StationTxSafetySupervisor supervisor,
        Func<StationTxSafetyResult, bool> predicate,
        string description,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = m_timeProvider.GetUtcNow() + timeout;
        StationTxSafetyResult last = new(
            false,
            "not-evaluated",
            string.Empty,
            supervisor.Snapshot);
        while (m_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await supervisor.EvaluateAsync(
                "hil-engine-connection-loss-watchdog",
                cancellationToken);
            if (predicate(last))
            {
                return last;
            }
            if (last.Snapshot.State == StationTxSafetyState.Faulted)
            {
                throw new InvalidOperationException(
                    $"The independent safety supervisor faulted while waiting for {description}: {last.Code}: {last.Message}");
            }
            await Task.Delay(PollInterval, m_timeProvider, cancellationToken);
        }
        throw new TimeoutException(
            $"Timed out waiting for {description}; last state was " +
            $"{last.Snapshot.State} ({last.Code}: {last.Message}).");
    }

    private async Task<StationTxGateResult> WaitForGateStateAsync(
        StationTxCommandGate gate,
        StationTxGateState expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = m_timeProvider.GetUtcNow() + timeout;
        StationTxGateResult last = new(
            false,
            "not-evaluated",
            string.Empty,
            gate.Snapshot);
        while (m_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await gate.EvaluateAsync(
                "hil-engine-connection-loss-engine-watchdog",
                cancellationToken);
            if (last.Snapshot.State == expected)
            {
                return last;
            }
            if (last.Snapshot.State == StationTxGateState.Faulted)
            {
                throw new InvalidOperationException(
                    $"The engine TX gate faulted: {last.Code}: {last.Message}");
            }
            await Task.Delay(PollInterval, m_timeProvider, cancellationToken);
        }
        throw new TimeoutException(
            $"The engine TX gate did not reach {expected}; last state was " +
            $"{last.Snapshot.State} ({last.Code}: {last.Message}).");
    }

    private HilFlexSession NewSession(string radioId) =>
        new(
            radioId,
            m_loggerFactory.CreateLogger<HilFlexSession>());

    private void VerifyInitialState(
        HilRadioSnapshot snapshot,
        string expectedSerial)
    {
        if (!string.Equals(snapshot.Serial, expectedSerial, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Connected radio serial '{snapshot.Serial}' does not match expected PSOC2 serial '{expectedSerial}'.");
        }
        if (snapshot.ExternalGuiClients.Count != 0)
        {
            throw new InvalidOperationException(
                "The engine command-channel-loss test requires every external GUI client to be disconnected.");
        }
        if (snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= m_timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException(
                "The engine command-channel-loss test requires a fresh idle interlock.");
        }
        if (snapshot.TransmitSettings is null)
        {
            throw new InvalidOperationException(
                "The radio did not report a complete restorable transmit-route snapshot.");
        }
        if (!snapshot.Cwx.HasFreshConfiguration(m_timeProvider.GetUtcNow()))
        {
            throw new InvalidOperationException(
                "The radio did not report fresh restorable CWX configuration.");
        }
    }

    private static void RequireSuccess(
        StationTxSafetyResult result,
        string operation)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Could not {operation}: {result.Code}: {result.Message}");
        }
    }

    private static void RequireConnectionSuccess(
        StationTxEngineConnectionResult result,
        string operation)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Could not {operation}: {result.Code}: {result.Message}");
        }
    }

    private sealed class DisconnectableTxCommandTransport(
        IStationTxCommandTransport inner,
        string engineInstanceId,
        string leaseId)
        : IStationTxCommandTransport
    {
        private int m_connectionLost;

        public bool IsConnected =>
            Volatile.Read(ref m_connectionLost) == 0 && inner.IsConnected;
        public uint ClientHandle => inner.ClientHandle;
        public int KeyCommands { get; private set; }
        public int UnkeyCommands { get; private set; }
        public int PostLossCommandAttempts { get; private set; }

        public StationTxEngineConnectionObservation Observation() =>
            new(
                engineInstanceId,
                leaseId,
                ClientHandle,
                IsConnected);

        public void InjectConnectionLoss() =>
            Interlocked.Exchange(ref m_connectionLost, 1);

        public async Task<StationTxTransportResult> SetTransmitAsync(
            bool enabled,
            uint expectedClientHandle,
            CancellationToken cancellationToken)
        {
            if (!IsConnected)
            {
                PostLossCommandAttempts++;
                return StationTxTransportResult.Rejected(
                    "The injected station-engine TX command channel is unavailable.");
            }
            if (enabled)
            {
                KeyCommands++;
            }
            else
            {
                UnkeyCommands++;
            }
            return await inner.SetTransmitAsync(
                enabled,
                expectedClientHandle,
                cancellationToken);
        }
    }

    private sealed class CountingEmergencyUnkeyTransport(
        IStationTxEmergencyUnkeyTransport inner)
        : IStationTxEmergencyUnkeyTransport
    {
        public bool IsConnected => inner.IsConnected;
        public int CommandCount { get; private set; }

        public async Task<StationTxTransportResult> RequestUnkeyAsync(
            CancellationToken cancellationToken)
        {
            CommandCount++;
            return await inner.RequestUnkeyAsync(cancellationToken);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
}
