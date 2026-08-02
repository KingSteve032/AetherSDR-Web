using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging;

namespace AetherSDR.TxHil;

internal enum HilSafetyOwnerLossKind
{
    BrowserSession,
    Authentication,
    GatewayProcess
}

internal sealed record HilSafetyOwnerLossProfile(
    HilSafetyOwnerLossKind Kind,
    string ManifestPurpose,
    string SignalReason,
    string ScenarioLabel,
    string PreflightTestName,
    string LiveTestName,
    string PreflightSessionId,
    string PreflightBrowserClientId,
    string PreflightEngineInstanceId,
    string LiveSessionId,
    string LiveBrowserClientId,
    string LiveEngineInstanceId,
    string OperatorDisplayName)
{
    public static HilSafetyOwnerLossProfile For(
        HilSafetyOwnerLossKind kind) =>
        kind switch
        {
            HilSafetyOwnerLossKind.BrowserSession => new(
                kind,
                HilArmManifest.SafetySessionLossPurpose,
                "browser-session-lost",
                "browser-session-loss",
                "independent-browser-session-loss-no-rf-preflight",
                "independent-browser-session-loss-unkey",
                "tx-hil-session-loss-preflight",
                "tx-hil-session-loss-browser",
                "tx-hil-session-loss-preflight-engine",
                "tx-hil-browser-session-loss",
                "tx-hil-browser-session-owner",
                "tx-hil-session-loss-engine-instance",
                "PSOC2 Browser Session Loss"),
            HilSafetyOwnerLossKind.Authentication => new(
                kind,
                HilArmManifest.SafetyAuthenticationLossPurpose,
                "authentication-lost",
                "authentication-loss",
                "independent-authentication-loss-no-rf-preflight",
                "independent-authentication-loss-unkey",
                "tx-hil-authentication-loss-preflight",
                "tx-hil-authentication-loss-browser",
                "tx-hil-authentication-loss-preflight-engine",
                "tx-hil-authentication-loss",
                "tx-hil-authentication-owner",
                "tx-hil-authentication-loss-engine-instance",
                "PSOC2 Authentication Loss"),
            HilSafetyOwnerLossKind.GatewayProcess => new(
                kind,
                HilArmManifest.SafetyGatewayProcessLossPurpose,
                "gateway-process-lost",
                "gateway-process-loss",
                "independent-gateway-process-loss-no-rf-preflight",
                "independent-gateway-process-loss-unkey",
                "tx-hil-gateway-process-loss-preflight",
                "tx-hil-gateway-process-loss-browser",
                "tx-hil-gateway-process-loss-preflight-engine",
                "tx-hil-gateway-process-loss",
                "tx-hil-gateway-process-owner",
                "tx-hil-gateway-process-loss-engine-instance",
                "PSOC2 Gateway Process Loss"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}

internal sealed class HilSafetySessionLossOperation(
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null,
    HilSafetyOwnerLossKind ownerLossKind =
        HilSafetyOwnerLossKind.BrowserSession)
{
    private static readonly TimeSpan HeartbeatTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(25);

    private readonly ILoggerFactory m_loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly TimeProvider m_timeProvider =
        timeProvider ?? TimeProvider.System;
    private readonly HilSafetyOwnerLossProfile m_profile =
        HilSafetyOwnerLossProfile.For(ownerLossKind);

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
        StationTxAuthenticationMonitor? authenticationMonitor = null;
        StationTxGatewayConnectionMonitor? gatewayMonitor = null;
        Process? gatewayProcess = null;
        Task<string>? gatewayErrors = null;
        HilGatewayAuthorityReady? gatewayReady = null;
        DateTimeOffset? gatewayExitedAt = null;
        CountingEmergencyUnkeyTransport? safetyTransport = null;
        TxLeaseManager? leases = null;
        TxLease? lease = null;
        string sessionId = m_profile.PreflightSessionId;
        string browserClientId = m_profile.PreflightBrowserClientId;
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
                    $"{m_profile.ScenarioLabel} preflight",
                    TimeSpan.FromSeconds(15),
                    out lease,
                    out string? leaseError))
            {
                throw new InvalidOperationException(
                    leaseError ?? "Could not acquire the preflight TX lease.");
            }

            safetyTransport = new CountingEmergencyUnkeyTransport(
                new HilEmergencyUnkeyTransport(observer));
            supervisor = new StationTxSafetySupervisor(
                options.RadioId,
                observer.OccupancyRegistry,
                safetyTransport,
                m_timeProvider);
            StationTxSafetyArm safetyArm = new(
                m_profile.PreflightEngineInstanceId,
                lease!.LeaseId,
                sessionId,
                browserClientId,
                engine.ClientHandle,
                HeartbeatTimeout);
            RequireSuccess(
                await supervisor.ArmAsync(safetyArm, cancellationToken),
                $"arm the {m_profile.ScenarioLabel} preflight supervisor");

            if (m_profile.Kind == HilSafetyOwnerLossKind.Authentication)
            {
                authenticationMonitor = new StationTxAuthenticationMonitor(
                    supervisor);
                StationTxAuthenticationResult authenticated =
                    await authenticationMonitor.EvaluateAsync(
                        AuthenticationObservation(safetyArm, true),
                        cancellationToken);
                if (!authenticated.Success ||
                    authenticated.Code != "authenticated")
                {
                    throw new InvalidOperationException(
                        "The preflight did not establish the exact authenticated authority before loss injection.");
                }
            }
            else if (m_profile.Kind == HilSafetyOwnerLossKind.GatewayProcess)
            {
                (gatewayProcess, gatewayErrors) = StartGatewayChild();
                gatewayReady = await ReadGatewayReadyAsync(
                    gatewayProcess,
                    gatewayErrors,
                    cancellationToken);
                gatewayMonitor = new StationTxGatewayConnectionMonitor(
                    supervisor);
                StationTxGatewayConnectionResult connected =
                    await gatewayMonitor.EvaluateAsync(
                        GatewayObservation(gatewayReady, safetyArm, true),
                        cancellationToken);
                if (!connected.Success || connected.Code != "gateway_connected")
                {
                    throw new InvalidOperationException(
                        "The preflight did not establish the exact gateway process before loss injection.");
                }
            }

            if (gatewayProcess is not null)
            {
                gatewayExitedAt = await KillGatewayChildAsync(
                    gatewayProcess,
                    cancellationToken);
            }
            int released = leases.ReleaseSession(
                sessionId,
                m_profile.SignalReason);
            if (released != 1 || leases.GetCurrent(options.RadioId) is not null)
            {
                throw new InvalidOperationException(
                    "The preflight did not release exactly one controlling-session TX lease.");
            }

            StationTxSafetyResult abort =
                await SignalOwnerLossAsync(
                    supervisor,
                    authenticationMonitor,
                    gatewayMonitor,
                    gatewayReady,
                    safetyArm,
                    cancellationToken);
            if (!abort.Success ||
                abort.Snapshot.State != StationTxSafetyState.Disarmed ||
                safetyTransport.CommandCount != 0)
            {
                throw new InvalidOperationException(
                    $"The idle {m_profile.ScenarioLabel} preflight did not disarm without issuing an unkey command.");
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                test = m_profile.PreflightTestName,
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
                sessionLoss = new
                {
                    releasedLeases = released,
                    exactSession = sessionId,
                    exactBrowserClient = browserClientId,
                    supervisorSignal = m_profile.SignalReason,
                    authenticatedTransitionObserved =
                        authenticationMonitor is not null,
                    gatewayProcessLossObserved = gatewayReady is not null,
                    gatewayProcess = gatewayReady is null
                        ? null
                        : new
                        {
                            gatewayReady.ProcessId,
                            gatewayReady.ProcessStartTime,
                            gatewayReady.GatewayInstanceId,
                            ExitedAt = gatewayExitedAt,
                            ExitCode = gatewayProcess?.ExitCode,
                            gatewayReady.RadioConnectionCreated,
                            gatewayReady.KeyCapability,
                            gatewayReady.UnkeyCapability
                        }
                },
                safety = new
                {
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
            if (authenticationMonitor is not null)
            {
                await authenticationMonitor.DisposeAsync();
            }
            if (gatewayMonitor is not null)
            {
                await gatewayMonitor.DisposeAsync();
            }
            await StopGatewayChildAsync(gatewayProcess, gatewayErrors);
            bool supervisorIdle = true;
            if (supervisor is not null)
            {
                supervisorIdle = await EmergencySupervisorUnkeyAsync(
                    supervisor,
                    cleanup.Token);
                await supervisor.DisposeAsync();
            }
            if (leases is not null && lease is not null)
            {
                leases.TryRelease(
                    options.RadioId,
                    lease.LeaseId,
                    sessionId,
                    browserClientId,
                    $"{m_profile.ScenarioLabel}-preflight-cleanup",
                    out _);
            }
            bool radioIdle = supervisorIdle &&
                await ConfirmRadioIdleAsync(
                    engine,
                    TimeSpan.FromSeconds(5),
                    cleanup.Token);
            if (!radioIdle)
            {
                m_loggerFactory.CreateLogger<HilSafetySessionLossOperation>()
                    .LogCritical(
                        "The no-RF {Scenario} preflight did not confirm idle; verify PSOC2 before continuing",
                        m_profile.ScenarioLabel);
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
                    m_loggerFactory.CreateLogger<HilSafetySessionLossOperation>()
                        .LogError(
                            exception,
                            "Could not restore transmit settings after the no-RF {Scenario} preflight",
                            m_profile.ScenarioLabel);
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
            m_profile.ManifestPurpose,
            m_timeProvider,
            cancellationToken);
        HilOptions options = m_profile.Kind switch
        {
            HilSafetyOwnerLossKind.Authentication =>
                HilArmManifest.ToSafetyAuthenticationLossOptions(
                    manifest,
                    commandLine.ArmFile,
                    commandLine.Token),
            HilSafetyOwnerLossKind.GatewayProcess =>
                HilArmManifest.ToSafetyGatewayProcessLossOptions(
                    manifest,
                    commandLine.ArmFile,
                    commandLine.Token),
            _ => HilArmManifest.ToSafetySessionLossOptions(
                manifest,
                commandLine.ArmFile,
                commandLine.Token)
        };

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
        StationTxAuthenticationMonitor? authenticationMonitor = null;
        StationTxGatewayConnectionMonitor? gatewayMonitor = null;
        Process? gatewayProcess = null;
        Task<string>? gatewayErrors = null;
        HilGatewayAuthorityReady? gatewayReady = null;
        DateTimeOffset? gatewayExitedAt = null;
        CountingTxCommandTransport? engineTransport = null;
        CountingEmergencyUnkeyTransport? safetyTransport = null;
        DateTimeOffset? keyedAt = null;
        DateTimeOffset? sessionLostAt = null;
        DateTimeOffset? safetyUnkeyRequestedAt = null;
        DateTimeOffset? idleAt = null;
        HilCwxIdentificationResult? identification = null;
        string sessionId = m_profile.LiveSessionId;
        string browserClientId = m_profile.LiveBrowserClientId;

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
                    m_profile.OperatorDisplayName,
                    TimeSpan.FromSeconds(15),
                    out lease,
                    out string? leaseError))
            {
                throw new InvalidOperationException(
                    leaseError ?? "Could not acquire the HIL TX lease.");
            }

            safetyTransport = new CountingEmergencyUnkeyTransport(
                new HilEmergencyUnkeyTransport(observer));
            supervisor = new StationTxSafetySupervisor(
                options.RadioId,
                observer.OccupancyRegistry,
                safetyTransport,
                m_timeProvider);
            StationTxSafetyArm safetyArm = new(
                m_profile.LiveEngineInstanceId,
                lease!.LeaseId,
                sessionId,
                browserClientId,
                engine.ClientHandle,
                HeartbeatTimeout);
            RequireSuccess(
                await supervisor.ArmAsync(safetyArm, cancellationToken),
                $"arm the independent {m_profile.ScenarioLabel} supervisor");

            if (m_profile.Kind == HilSafetyOwnerLossKind.Authentication)
            {
                authenticationMonitor = new StationTxAuthenticationMonitor(
                    supervisor);
                StationTxAuthenticationResult authenticated =
                    await authenticationMonitor.EvaluateAsync(
                        AuthenticationObservation(safetyArm, true),
                        cancellationToken);
                if (!authenticated.Success ||
                    authenticated.Code != "authenticated")
                {
                    throw new InvalidOperationException(
                        "The live test did not establish the exact authenticated authority before keying.");
                }
            }
            else if (m_profile.Kind == HilSafetyOwnerLossKind.GatewayProcess)
            {
                (gatewayProcess, gatewayErrors) = StartGatewayChild();
                gatewayReady = await ReadGatewayReadyAsync(
                    gatewayProcess,
                    gatewayErrors,
                    cancellationToken);
                gatewayMonitor = new StationTxGatewayConnectionMonitor(
                    supervisor);
                StationTxGatewayConnectionResult connected =
                    await gatewayMonitor.EvaluateAsync(
                        GatewayObservation(gatewayReady, safetyArm, true),
                        cancellationToken);
                if (!connected.Success || connected.Code != "gateway_connected")
                {
                    throw new InvalidOperationException(
                        "The live test did not establish the exact gateway process before keying.");
                }
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

            if (gatewayProcess is not null)
            {
                gatewayExitedAt = await KillGatewayChildAsync(
                    gatewayProcess,
                    cancellationToken);
            }
            int released = leases.ReleaseSession(
                sessionId,
                m_profile.SignalReason);
            sessionLostAt = gatewayExitedAt ?? m_timeProvider.GetUtcNow();
            if (released != 1 || leases.GetCurrent(options.RadioId) is not null)
            {
                throw new InvalidOperationException(
                    "The live test did not release exactly one controlling-session TX lease.");
            }

            StationTxSafetyResult abort =
                await SignalOwnerLossAsync(
                    supervisor,
                    authenticationMonitor,
                    gatewayMonitor,
                    gatewayReady,
                    safetyArm,
                    cancellationToken);
            if (!abort.Success ||
                abort.Snapshot.State != StationTxSafetyState.UnkeyPending ||
                safetyTransport.CommandCount != 1)
            {
                throw new InvalidOperationException(
                    $"The independent observer did not issue the exact {m_profile.ScenarioLabel} unkey: {abort.Code}: {abort.Message}");
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
                    $"The {m_profile.ScenarioLabel} command split was not exact: the engine must key once and never unkey, while the independent observer must unkey exactly once.");
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
                test = m_profile.LiveTestName,
                passed = true,
                radio = options.RadioId,
                serial = initial.Serial,
                frequencyHz = options.FrequencyHz,
                txAntenna = options.TxAntenna,
                rfPower = options.RfPower,
                engineClientHandle = $"0x{engine.ClientHandle:x8}",
                observerClientHandle = $"0x{observer.ClientHandle:x8}",
                sessionLoss = new
                {
                    exactSession = sessionId,
                    exactBrowserClient = browserClientId,
                    releasedLeases = 1,
                    signaledReason = m_profile.SignalReason,
                    authenticatedTransitionObserved =
                        authenticationMonitor is not null,
                    gatewayProcessLossObserved = gatewayReady is not null,
                    gatewayProcess = gatewayReady is null
                        ? null
                        : new
                        {
                            gatewayReady.ProcessId,
                            gatewayReady.ProcessStartTime,
                            gatewayReady.GatewayInstanceId,
                            ExitedAt = gatewayExitedAt,
                            ExitCode = gatewayProcess?.ExitCode,
                            gatewayReady.RadioConnectionCreated,
                            gatewayReady.KeyCapability,
                            gatewayReady.UnkeyCapability
                        }
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
                    sessionLostAt,
                    safetyUnkeyRequestedAt,
                    idleAt,
                    sessionLossToUnkeyRequestMilliseconds =
                        (safetyUnkeyRequestedAt!.Value -
                         sessionLostAt!.Value).TotalMilliseconds,
                    unkeyRequestToIdleMilliseconds =
                        (idleAt!.Value -
                         safetyUnkeyRequestedAt.Value).TotalMilliseconds,
                    keyedToIdleMilliseconds =
                        (idleAt.Value - keyedAt!.Value).TotalMilliseconds
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

            if (authenticationMonitor is not null)
            {
                await authenticationMonitor.DisposeAsync();
            }
            if (gatewayMonitor is not null)
            {
                await gatewayMonitor.DisposeAsync();
            }
            await StopGatewayChildAsync(gatewayProcess, gatewayErrors);
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
                    $"hil-{m_profile.ScenarioLabel}-cleanup",
                    out _);
            }

            bool radioIdle = supervisorIdle &&
                gateIdle &&
                await ConfirmRadioIdleAsync(
                    engine,
                    TimeSpan.FromSeconds(5),
                    cleanup.Token);
            if (!radioIdle)
            {
                m_loggerFactory.CreateLogger<HilSafetySessionLossOperation>()
                    .LogCritical(
                        "PSOC2 did not provide fresh idle confirmation after the {Scenario} test. Keep RF power bounded and use the remote power kill immediately",
                        m_profile.ScenarioLabel);
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
                        m_loggerFactory.CreateLogger<HilSafetySessionLossOperation>()
                            .LogCritical(
                                exception,
                                "The {Scenario} test emitted RF but automatic cleanup identification failed; identify KC4CAW manually as soon as possible",
                                m_profile.ScenarioLabel);
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
                    m_loggerFactory.CreateLogger<HilSafetySessionLossOperation>()
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

    private static StationTxAuthenticationObservation
        AuthenticationObservation(
            StationTxSafetyArm arm,
            bool isAuthenticated) =>
        new(
            arm.EngineInstanceId,
            arm.LeaseId,
            arm.SessionId,
            arm.BrowserClientId,
            arm.ProtectedClientHandle,
            isAuthenticated);

    private static StationTxGatewayConnectionObservation GatewayObservation(
        HilGatewayAuthorityReady gateway,
        StationTxSafetyArm arm,
        bool isConnected) =>
        new(
            gateway.GatewayInstanceId,
            arm.EngineInstanceId,
            arm.LeaseId,
            arm.SessionId,
            arm.BrowserClientId,
            arm.ProtectedClientHandle,
            isConnected);

    private static (Process Child, Task<string> Errors) StartGatewayChild()
    {
        string entryAssembly =
            Assembly.GetEntryAssembly()?.Location ??
            throw new InvalidOperationException(
                "The HIL entry assembly path is unavailable.");
        string processPath =
            Environment.ProcessPath ??
            throw new InvalidOperationException(
                "The HIL process executable path is unavailable.");
        ProcessStartInfo start = new()
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            FileName = processPath
        };
        bool dotnetHost = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
        if (dotnetHost)
        {
            start.ArgumentList.Add(entryAssembly);
        }
        start.ArgumentList.Add("internal-gateway-authority-child");
        Process child = Process.Start(start) ??
            throw new InvalidOperationException(
                "The HIL gateway-authority child process could not be started.");
        return (child, child.StandardError.ReadToEndAsync());
    }

    private static async Task<HilGatewayAuthorityReady> ReadGatewayReadyAsync(
        Process child,
        Task<string> errors,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        string? line;
        try
        {
            line = await child.StandardOutput.ReadLineAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Timed out waiting for the gateway-authority child process.");
        }
        if (line is null)
        {
            string stderr = await errors;
            throw new InvalidOperationException(
                $"The gateway-authority child exited before ready. Exit={child.ExitCode}; stderr={stderr}");
        }
        HilGatewayAuthorityReady ready =
            JsonSerializer.Deserialize<HilGatewayAuthorityReady>(line) ??
            throw new InvalidOperationException(
                "The gateway-authority child returned invalid readiness evidence.");
        if (!string.Equals(
                ready.Event,
                "gateway-authority-ready",
                StringComparison.Ordinal) ||
            ready.ProcessId != child.Id ||
            ready.RadioConnectionCreated ||
            ready.KeyCapability ||
            ready.UnkeyCapability ||
            string.IsNullOrWhiteSpace(ready.GatewayInstanceId))
        {
            throw new InvalidOperationException(
                "The gateway-authority child readiness evidence failed closed.");
        }
        return ready;
    }

    private async Task<DateTimeOffset> KillGatewayChildAsync(
        Process child,
        CancellationToken cancellationToken)
    {
        if (child.HasExited)
        {
            throw new InvalidOperationException(
                "The gateway-authority child exited before loss injection.");
        }
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync(cancellationToken);
        if (child.ExitCode == 0)
        {
            throw new InvalidOperationException(
                "The gateway-authority child exited gracefully instead of being force-killed.");
        }
        return m_timeProvider.GetUtcNow();
    }

    private static async Task StopGatewayChildAsync(
        Process? child,
        Task<string>? errors)
    {
        if (child is null)
        {
            return;
        }
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            if (errors is not null)
            {
                _ = await errors;
            }
        }
        catch
        {
        }
        finally
        {
            child.Dispose();
        }
    }

    private async Task<StationTxSafetyResult> SignalOwnerLossAsync(
        StationTxSafetySupervisor supervisor,
        StationTxAuthenticationMonitor? authenticationMonitor,
        StationTxGatewayConnectionMonitor? gatewayMonitor,
        HilGatewayAuthorityReady? gatewayReady,
        StationTxSafetyArm arm,
        CancellationToken cancellationToken)
    {
        if (m_profile.Kind == HilSafetyOwnerLossKind.BrowserSession)
        {
            return await supervisor.AbortAsync(
                m_profile.SignalReason,
                cancellationToken);
        }
        if (m_profile.Kind == HilSafetyOwnerLossKind.Authentication)
        {
            if (authenticationMonitor is null)
            {
                throw new InvalidOperationException(
                    "The authentication-loss operation has no exact-identity authentication monitor.");
            }
            StationTxAuthenticationResult authenticationLoss =
                await authenticationMonitor.EvaluateAsync(
                    AuthenticationObservation(arm, false),
                    cancellationToken);
            return new StationTxSafetyResult(
                authenticationLoss.Success,
                authenticationLoss.Code,
                authenticationLoss.Message,
                authenticationLoss.SafetySnapshot);
        }
        if (gatewayMonitor is null || gatewayReady is null)
        {
            throw new InvalidOperationException(
                "The gateway-process-loss operation has no exact process monitor.");
        }
        StationTxGatewayConnectionResult gatewayLoss =
            await gatewayMonitor.EvaluateAsync(
                GatewayObservation(gatewayReady, arm, false),
                cancellationToken);
        return new StationTxSafetyResult(
            gatewayLoss.Success,
            gatewayLoss.Code,
            gatewayLoss.Message,
            gatewayLoss.SafetySnapshot);
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
            m_loggerFactory.CreateLogger<HilSafetySessionLossOperation>()
                .LogCritical(
                    exception,
                    "The independent safety observer could not confirm emergency unkey during {Scenario} cleanup",
                    m_profile.ScenarioLabel);
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
            m_loggerFactory.CreateLogger<HilSafetySessionLossOperation>()
                .LogCritical(
                    exception,
                    "The engine fallback could not confirm emergency unkey during {Scenario} cleanup",
                    m_profile.ScenarioLabel);
            return false;
        }
    }

    private async Task<bool> ConfirmRadioIdleAsync(
        HilFlexSession session,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.WaitForAsync(
                snapshot =>
                    snapshot.TxOccupancy.State == RadioTxOccupancyState.Idle &&
                    snapshot.TxOccupancy.FreshUntil > m_timeProvider.GetUtcNow(),
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
                $"hil-{m_profile.ScenarioLabel}-watchdog",
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
                $"hil-{m_profile.ScenarioLabel}-engine-watchdog",
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
                $"The {m_profile.ScenarioLabel} test requires every external GUI client to be disconnected.");
        }
        if (snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= m_timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException(
                $"The {m_profile.ScenarioLabel} test requires a fresh idle interlock.");
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
            uint expectedProtectedClientHandle,
            CancellationToken cancellationToken)
        {
            CommandCount++;
            return await inner.RequestUnkeyAsync(
                expectedProtectedClientHandle,
                cancellationToken);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
}
