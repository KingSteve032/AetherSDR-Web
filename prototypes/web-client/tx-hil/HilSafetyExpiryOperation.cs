using System.Text.Json;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging;

namespace AetherSDR.TxHil;

internal sealed class HilSafetyExpiryOperation(
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan InitialHeartbeatTimeout =
        TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ExpiringHeartbeatTimeout =
        TimeSpan.FromMilliseconds(750);
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
        StationTxSafetySupervisor? supervisor = null;
        CountingEmergencyUnkeyTransport? safetyTransport = null;
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
                    "The transmit settings changed after the initial safety snapshot.");
            }
            await engine.RequestLocalPttAsync(cancellationToken);

            await observer.WaitForAsync(
                snapshot =>
                    snapshot.TxOccupancy.State == RadioTxOccupancyState.Idle &&
                    snapshot.TxOccupancy.FreshUntil >
                        m_timeProvider.GetUtcNow() &&
                    snapshot.TxOccupancy.LocalPttOwners.Count == 1 &&
                    snapshot.TxOccupancy.LocalPttOwners[0].ClientHandle ==
                        engine.ClientHandle &&
                    snapshot.GuiClients.Any(client =>
                        client.ClientHandle == engine.ClientHandle &&
                        client.LocalPtt),
                TimeSpan.FromSeconds(5),
                cancellationToken);

            safetyTransport = new CountingEmergencyUnkeyTransport(
                new HilEmergencyUnkeyTransport(observer));
            supervisor = new StationTxSafetySupervisor(
                options.RadioId,
                observer.OccupancyRegistry,
                safetyTransport,
                m_timeProvider);
            StationTxSafetyArm safetyArm = new(
                "tx-hil-preflight-engine",
                "tx-hil-preflight-lease",
                "tx-hil-preflight-session",
                "tx-hil-preflight-browser",
                engine.ClientHandle,
                InitialHeartbeatTimeout);
            RequireSuccess(
                await supervisor.ArmAsync(safetyArm, cancellationToken),
                "arm the preflight safety supervisor");
            RequireSuccess(
                await supervisor.HeartbeatAsync(
                    safetyArm.EngineInstanceId,
                    safetyArm.LeaseId,
                    safetyArm.ProtectedClientHandle,
                    safetyArm.HeartbeatTimeout,
                    cancellationToken),
                "renew the preflight safety heartbeat");
            StationTxSafetyResult idle = await supervisor.EvaluateAsync(
                "safety-expiry-preflight",
                cancellationToken);
            if (!idle.Success || idle.Code != "armed_idle")
            {
                throw new InvalidOperationException(
                    $"The safety supervisor did not remain armed and idle: {idle.Code}: {idle.Message}");
            }
            StationTxSafetyResult disarmed = await supervisor.AbortAsync(
                "preflight-complete",
                cancellationToken);
            if (!disarmed.Success ||
                disarmed.Snapshot.State != StationTxSafetyState.Disarmed ||
                safetyTransport.CommandCount != 0)
            {
                throw new InvalidOperationException(
                    "The safety-expiry preflight did not disarm from idle without issuing unkey.");
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                test = "independent-heartbeat-expiry-no-rf-preflight",
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
                safety = new
                {
                    heartbeatExpiryMilliseconds =
                        ExpiringHeartbeatTimeout.TotalMilliseconds,
                    unkeyOnly = true,
                    unkeyCommands = safetyTransport.CommandCount,
                    keyCapability = false
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
                await supervisor.DisposeAsync();
            }
            bool radioIdle = supervisorIdle &&
                await ConfirmRadioIdleAsync(
                    engine,
                    TimeSpan.FromSeconds(5),
                    cleanup.Token);
            if (!radioIdle)
            {
                m_loggerFactory.CreateLogger<HilSafetyExpiryOperation>()
                    .LogCritical(
                        "The no-RF safety-expiry preflight did not confirm idle; verify PSOC2 before continuing");
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
                    m_loggerFactory.CreateLogger<HilSafetyExpiryOperation>()
                        .LogError(
                            exception,
                            "Could not restore transmit settings after the no-RF safety-expiry preflight");
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
            HilArmManifest.SafetyExpiryPurpose,
            m_timeProvider,
            cancellationToken);
        HilOptions options = HilArmManifest.ToSafetyExpiryOptions(
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
        CountingTxCommandTransport? engineTransport = null;
        CountingEmergencyUnkeyTransport? safetyTransport = null;
        DateTimeOffset? keyedAt = null;
        DateTimeOffset? safetyUnkeyRequestedAt = null;
        DateTimeOffset? idleAt = null;
        HilCwxIdentificationResult? identification = null;
        bool radioIdle = true;
        const string sessionId = "tx-hil-safety-expiry";
        const string browserClientId = "tx-hil-safety-owner";

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

            await observer.WaitForAsync(
                snapshot =>
                    snapshot.TxOccupancy.State == RadioTxOccupancyState.Idle &&
                    snapshot.TxOccupancy.FreshUntil >
                        m_timeProvider.GetUtcNow() &&
                    snapshot.TxOccupancy.LocalPttOwners.Count == 1 &&
                    snapshot.TxOccupancy.LocalPttOwners[0].ClientHandle ==
                        engine.ClientHandle &&
                    snapshot.GuiClients.Any(client =>
                        client.ClientHandle == engine.ClientHandle &&
                        client.LocalPtt),
                TimeSpan.FromSeconds(5),
                cancellationToken);

            safetyTransport = new CountingEmergencyUnkeyTransport(
                new HilEmergencyUnkeyTransport(observer));
            supervisor = new StationTxSafetySupervisor(
                options.RadioId,
                observer.OccupancyRegistry,
                safetyTransport,
                m_timeProvider);
            StationTxSafetyArm safetyArm = new(
                "tx-hil-engine-instance",
                "tx-hil-safety-lease",
                sessionId,
                browserClientId,
                engine.ClientHandle,
                InitialHeartbeatTimeout);
            StationTxSafetyResult armed = await supervisor.ArmAsync(
                safetyArm,
                cancellationToken);
            RequireSuccess(armed, "arm the independent safety supervisor");

            leases = new TxLeaseManager(m_timeProvider);
            if (!leases.TryAcquire(
                    options.RadioId,
                    sessionId,
                    browserClientId,
                    "tx-hil",
                    "PSOC2 Independent Safety Expiry",
                    TimeSpan.FromSeconds(15),
                    out lease,
                    out string? leaseError))
            {
                throw new InvalidOperationException(
                    leaseError ?? "Could not acquire the HIL TX lease.");
            }

            engineTransport = new CountingTxCommandTransport(
                resources.Transport);
            gate = new StationTxCommandGate(
                allowTransmit: true,
                options.RadioId,
                leases,
                engine.OccupancyRegistry,
                engineTransport,
                m_timeProvider);

            StationTxGateResult key = await gate.RequestKeyAsync(
                lease!.LeaseId,
                sessionId,
                browserClientId,
                cancellationToken);
            if (!key.Success && key.Snapshot.State != StationTxGateState.KeyPending)
            {
                throw new InvalidOperationException(
                    $"The key request failed closed: {key.Code}: {key.Message}");
            }
            StationTxGateResult keyed = await WaitForGateStateAsync(
                gate,
                StationTxGateState.Keyed,
                TimeSpan.FromSeconds(3),
                cancellationToken);
            keyedAt = m_timeProvider.GetUtcNow();

            StationTxSafetyResult protectedTx =
                await WaitForSafetyAsync(
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

            StationTxSafetyResult finalHeartbeat =
                await supervisor.HeartbeatAsync(
                    safetyArm.EngineInstanceId,
                    safetyArm.LeaseId,
                    safetyArm.ProtectedClientHandle,
                    ExpiringHeartbeatTimeout,
                    cancellationToken);
            RequireSuccess(finalHeartbeat, "renew the final bounded heartbeat");

            StationTxSafetyResult unkeyPending =
                await WaitForSafetyAsync(
                    supervisor,
                    result =>
                        result.Snapshot.State ==
                            StationTxSafetyState.UnkeyPending &&
                        safetyTransport.CommandCount == 1,
                    "heartbeat expiry to issue the independent emergency unkey",
                    TimeSpan.FromSeconds(3),
                    cancellationToken);
            RequireSuccess(
                unkeyPending,
                "issue the independent emergency unkey");
            safetyUnkeyRequestedAt = m_timeProvider.GetUtcNow();

            StationTxSafetyResult safetyIdle =
                await WaitForSafetyAsync(
                    supervisor,
                    result =>
                        result.Snapshot.State ==
                            StationTxSafetyState.Disarmed &&
                        result.Code is "unkeyed" or "disarmed",
                    "independent observer to confirm radio idle",
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            RequireSuccess(safetyIdle, "confirm independent-observer idle");

            StationTxGateResult engineIdle = await WaitForGateStateAsync(
                gate,
                StationTxGateState.Idle,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            idleAt = m_timeProvider.GetUtcNow();

            if (engineTransport.KeyCommands != 1 ||
                engineTransport.UnkeyCommands != 0 ||
                safetyTransport.CommandCount != 1)
            {
                throw new InvalidOperationException(
                    "The forced-unkey command split was not exact: the engine must key once and never unkey, while the independent observer must unkey exactly once.");
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
                test = "independent-heartbeat-expiry-unkey",
                passed = true,
                radio = options.RadioId,
                serial = initial.Serial,
                frequencyHz = options.FrequencyHz,
                txAntenna = options.TxAntenna,
                rfPower = options.RfPower,
                engineClientHandle = $"0x{engine.ClientHandle:x8}",
                observerClientHandle = $"0x{observer.ClientHandle:x8}",
                heartbeat = new
                {
                    initialMilliseconds =
                        InitialHeartbeatTimeout.TotalMilliseconds,
                    expiringMilliseconds =
                        ExpiringHeartbeatTimeout.TotalMilliseconds
                },
                engineCommands = new
                {
                    key = engineTransport.KeyCommands,
                    unkey = engineTransport.UnkeyCommands
                },
                independentObserverCommands = new
                {
                    unkey = safetyTransport.CommandCount,
                    keyCapability = false
                },
                timing = new
                {
                    keyedAt,
                    safetyUnkeyRequestedAt,
                    idleAt,
                    keyedToIdleMilliseconds =
                        (idleAt!.Value - keyedAt!.Value).TotalMilliseconds
                },
                gate = new
                {
                    state = engineIdle.Snapshot.State.ToString(),
                    engineIdle.Snapshot.Reason
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
                await supervisor.DisposeAsync();
            }

            bool gateIdle = true;
            if (gate is not null)
            {
                gateIdle = await EmergencyGateUnkeyAsync(
                    gate,
                    lease,
                    sessionId,
                    browserClientId,
                    cleanup.Token);
                await gate.DisposeAsync();
            }
            if (leases is not null && lease is not null)
            {
                leases.TryRelease(
                    options.RadioId,
                    lease.LeaseId,
                    sessionId,
                    browserClientId,
                    "hil-safety-expiry-cleanup",
                    out _);
            }

            radioIdle = supervisorIdle &&
                gateIdle &&
                await ConfirmRadioIdleAsync(
                    engine,
                    TimeSpan.FromSeconds(5),
                    cleanup.Token);
            if (!radioIdle)
            {
                m_loggerFactory.CreateLogger<HilSafetyExpiryOperation>()
                    .LogCritical(
                        "PSOC2 did not provide fresh idle confirmation after the independent safety-expiry test. Keep RF power bounded and use the remote power kill immediately");
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
                            safeId.TxOccupancy.State ==
                                RadioTxOccupancyState.Idle &&
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
                        m_loggerFactory.CreateLogger<HilSafetyExpiryOperation>()
                            .LogCritical(
                                exception,
                                "The forced-unkey test emitted RF but automatic cleanup identification failed; identify KC4CAW manually as soon as possible");
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
                    m_loggerFactory.CreateLogger<HilSafetyExpiryOperation>()
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
                result =>
                    result.Snapshot.State ==
                        StationTxSafetyState.Disarmed,
                "safety supervisor cleanup idle",
                TimeSpan.FromSeconds(5),
                cancellationToken);
            return idle.Success;
        }
        catch (Exception exception)
        {
            m_loggerFactory.CreateLogger<HilSafetyExpiryOperation>()
                .LogCritical(
                    exception,
                    "The independent safety observer could not confirm emergency unkey during cleanup");
            return false;
        }
    }

    private async Task<bool> EmergencyGateUnkeyAsync(
        StationTxCommandGate gate,
        TxLease? lease,
        string sessionId,
        string browserClientId,
        CancellationToken cancellationToken)
    {
        if (!gate.Snapshot.HasActiveIntent || lease is null)
        {
            return gate.Snapshot.State == StationTxGateState.Idle;
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
            return true;
        }
        catch (Exception exception)
        {
            m_loggerFactory.CreateLogger<HilSafetyExpiryOperation>()
                .LogCritical(
                    exception,
                    "The engine fallback could not confirm emergency unkey during cleanup");
            return false;
        }
    }

    private static async Task<bool> ConfirmRadioIdleAsync(
        HilFlexSession session,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.WaitForAsync(
                snapshot =>
                    snapshot.TxOccupancy.State ==
                        RadioTxOccupancyState.Idle &&
                    snapshot.TxOccupancy.FreshUntil >
                        DateTimeOffset.UtcNow,
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
                "hil-independent-watchdog",
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
            await Task.Delay(
                PollInterval,
                m_timeProvider,
                cancellationToken);
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
                "hil-engine-watchdog",
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
            await Task.Delay(
                PollInterval,
                m_timeProvider,
                cancellationToken);
        }
        throw new TimeoutException(
            $"The engine TX gate did not reach {expected}; last state was " +
            $"{last.Snapshot.State} ({last.Code}: {last.Message}).");
    }

    private HilFlexSession NewSession(string radioId) =>
        new(
            radioId,
            m_loggerFactory.CreateLogger<HilFlexSession>());

    private static void VerifyInitialState(
        HilRadioSnapshot snapshot,
        string expectedSerial)
    {
        if (!string.Equals(
                snapshot.Serial,
                expectedSerial,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Connected radio serial '{snapshot.Serial}' does not match expected PSOC2 serial '{expectedSerial}'.");
        }
        if (snapshot.ExternalGuiClients.Count != 0)
        {
            throw new InvalidOperationException(
                "The independent safety-expiry test requires every external GUI client to be disconnected.");
        }
        if (snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException(
                "The independent safety-expiry test requires a fresh idle interlock.");
        }
        if (snapshot.TransmitSettings is null)
        {
            throw new InvalidOperationException(
                "The radio did not report a complete restorable transmit-route snapshot.");
        }
        if (!snapshot.Cwx.HasFreshConfiguration(DateTimeOffset.UtcNow))
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

    private sealed class CountingTxCommandTransport(
        IStationTxCommandTransport inner)
        : IStationTxCommandTransport
    {
        public bool IsConnected => inner.IsConnected;
        public uint ClientHandle => inner.ClientHandle;
        public int KeyCommands { get; private set; }
        public int UnkeyCommands { get; private set; }

        public async Task<StationTxTransportResult> SetTransmitAsync(
            bool enabled,
            uint expectedClientHandle,
            CancellationToken cancellationToken)
        {
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
