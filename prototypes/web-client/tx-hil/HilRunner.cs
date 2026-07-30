using System.Text.Json;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging;

namespace AetherSDR.TxHil;

internal sealed class HilRunner(
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null)
{
    private readonly ILoggerFactory m_loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly TimeProvider m_timeProvider =
        timeProvider ?? TimeProvider.System;

    public async Task<int> RunAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        return options.Command switch
        {
            HilCommand.Inspect =>
                await InspectAsync(options, cancellationToken),
            HilCommand.RestoreIdleDefaults =>
                await RestoreIdleDefaultsAsync(options, cancellationToken),
            HilCommand.VerifyExternalBlock =>
                await VerifyExternalBlockAsync(options, cancellationToken),
            HilCommand.VerifyCwxConfiguration =>
                await VerifyCwxConfigurationAsync(options, cancellationToken),
            HilCommand.VerifyPreflight =>
                await VerifyPreflightAsync(options, cancellationToken),
            HilCommand.VerifySafetyFaults =>
                await VerifySafetyFaultsAsync(cancellationToken),
            HilCommand.VerifySafetyObserver =>
                await VerifySafetyObserverAsync(options, cancellationToken),
            HilCommand.VerifySafetyExpiryPreflight =>
                await VerifySafetyExpiryPreflightAsync(
                    options,
                    cancellationToken),
            HilCommand.VerifySafetySessionLossPreflight =>
                await VerifySafetySessionLossPreflightAsync(
                    options,
                    cancellationToken),
            HilCommand.VerifySafetyEngineConnectionLossPreflight =>
                await VerifySafetyEngineConnectionLossPreflightAsync(
                    options,
                    cancellationToken),
            HilCommand.VerifySafetyProcessLossPreflight =>
                await VerifySafetyProcessLossPreflightAsync(
                    options,
                    cancellationToken),
            HilCommand.Prepare =>
                await PrepareAsync(
                    options,
                    HilArmManifest.NormalPulsePurpose,
                    "operator-unkey pulse",
                    cancellationToken),
            HilCommand.Pulse =>
                await PulseAsync(options, cancellationToken),
            HilCommand.PrepareSafetyExpiry =>
                await PrepareAsync(
                    options,
                    HilArmManifest.SafetyExpiryPurpose,
                    "independent heartbeat-expiry test",
                    cancellationToken),
            HilCommand.SafetyExpiry =>
                await SafetyExpiryAsync(options, cancellationToken),
            HilCommand.PrepareSafetySessionLoss =>
                await PrepareAsync(
                    options,
                    HilArmManifest.SafetySessionLossPurpose,
                    "independent browser-session-loss test",
                    cancellationToken),
            HilCommand.SafetySessionLoss =>
                await SafetySessionLossAsync(options, cancellationToken),
            HilCommand.PrepareSafetyEngineConnectionLoss =>
                await PrepareAsync(
                    options,
                    HilArmManifest.SafetyEngineConnectionLossPurpose,
                    "independent station-engine TX command-channel loss test",
                    cancellationToken),
            HilCommand.SafetyEngineConnectionLoss =>
                await SafetyEngineConnectionLossAsync(
                    options,
                    cancellationToken),
            HilCommand.PrepareSafetyProcessLoss =>
                await PrepareAsync(
                    options,
                    HilArmManifest.SafetyProcessLossPurpose,
                    "independent station-engine process/TCP loss test",
                    cancellationToken),
            HilCommand.SafetyProcessLoss =>
                await SafetyProcessLossAsync(
                    options,
                    cancellationToken),
            _ => throw new InvalidOperationException("Unsupported HIL command.")
        };
    }

    private static async Task<int> VerifySafetyFaultsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HilSafetyFaultScenario> scenarios =
            await HilSafetyFaultMatrix.RunAsync(cancellationToken);
        HilSafetyFaultScenario[] failed = scenarios
            .Where(scenario => !scenario.Passed)
            .ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            test = "station-safety-fault-matrix",
            passed = failed.Length == 0,
            scenarioCount = scenarios.Count,
            unkeyOnly = true,
            radioConnectionCreated = false,
            scenarios
        }, JsonOptions));
        if (failed.Length != 0)
        {
            throw new InvalidOperationException(
                "One or more station safety fault scenarios failed: " +
                string.Join(", ", failed.Select(scenario => scenario.Name)));
        }
        return 0;
    }

    private async Task<int> VerifySafetyObserverAsync(
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
        HilRadioSnapshot engineSnapshot = engine.Snapshot();
        VerifyIdentity(engineSnapshot, options.ExpectedSerial);
        if (engineSnapshot.ExternalGuiClients.Count != 0 ||
            engineSnapshot.TxOccupancy.State != RadioTxOccupancyState.Idle)
        {
            throw new InvalidOperationException(
                "The live safety-observer preflight requires PSOC2 idle with no external GUI client.");
        }

        await engine.RequestLocalPttAsync(cancellationToken);
        await observer.WaitForAsync(
            snapshot =>
                snapshot.TxOccupancy.State == RadioTxOccupancyState.Idle &&
                snapshot.GuiClients.Any(client =>
                    client.ClientHandle == engine.ClientHandle &&
                    client.LocalPtt) &&
                snapshot.TxOccupancy.LocalPttOwners.Count == 1 &&
                snapshot.TxOccupancy.LocalPttOwners[0].ClientHandle ==
                    engine.ClientHandle,
            TimeSpan.FromSeconds(5),
            cancellationToken);

        CountingEmergencyUnkeyTransport transport = new(
            new HilEmergencyUnkeyTransport(observer));
        await using StationTxSafetySupervisor supervisor = new(
            options.RadioId,
            observer.OccupancyRegistry,
            transport,
            m_timeProvider);
        StationTxSafetyArm arm = new(
            "hil-engine-preflight",
            "hil-lease-preflight",
            "hil-session-preflight",
            "hil-browser-preflight",
            engine.ClientHandle,
            TimeSpan.FromSeconds(2));
        StationTxSafetyResult armed = await supervisor.ArmAsync(
            arm,
            cancellationToken);
        if (!armed.Success)
        {
            throw new InvalidOperationException(
                $"The live safety observer could not arm: {armed.Code}: {armed.Message}");
        }
        StationTxSafetyResult heartbeat = await supervisor.HeartbeatAsync(
            arm.EngineInstanceId,
            arm.LeaseId,
            arm.ProtectedClientHandle,
            arm.HeartbeatTimeout,
            cancellationToken);
        if (!heartbeat.Success)
        {
            throw new InvalidOperationException(
                $"The live safety observer rejected its exact heartbeat: {heartbeat.Code}: {heartbeat.Message}");
        }
        StationTxSafetyResult idle = await supervisor.EvaluateAsync(
            "live-observer-preflight",
            cancellationToken);
        if (!idle.Success || idle.Code != "armed_idle")
        {
            throw new InvalidOperationException(
                $"The live safety observer did not remain armed and idle: {idle.Code}: {idle.Message}");
        }
        StationTxSafetyResult disarmed = await supervisor.AbortAsync(
            "preflight-complete",
            cancellationToken);
        if (!disarmed.Success ||
            disarmed.Snapshot.State != StationTxSafetyState.Disarmed ||
            transport.CommandCount != 0)
        {
            throw new InvalidOperationException(
                "The live safety-observer preflight did not disarm from idle without issuing unkey.");
        }

        HilRadioSnapshot observerSnapshot = observer.Snapshot();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            test = "live-non-gui-safety-observer",
            passed = true,
            radio = options.RadioId,
            serial = engineSnapshot.Serial,
            engineClientHandle = $"0x{engine.ClientHandle:x8}",
            observerClientHandle = $"0x{observer.ClientHandle:x8}",
            observerGuiRegistered = observer.GuiRegistered,
            engineLocalPttObserved = true,
            interlock = observerSnapshot.TxOccupancy.StateName,
            unkeyOnlyBoundary = true,
            unkeyCommands = transport.CommandCount,
            keyCommandAvailable = false
        }, JsonOptions));
        return 0;
    }

    private async Task<int> InspectAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession session = NewSession(options.RadioId);
        await session.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: true,
            cancellationToken);
        HilRadioSnapshot snapshot = session.Snapshot();
        VerifyIdentity(snapshot, options.ExpectedSerial);
        WriteSnapshot("inspect", snapshot);
        return 0;
    }

    private async Task<int> RestoreIdleDefaultsAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession session =
            await ConnectRecoveryGuiWithRetriesAsync(
                options,
                cancellationToken);
        HilRadioSnapshot before = session.Snapshot();
        VerifyIdentity(before, options.ExpectedSerial);
        if (before.ExternalGuiClients.Count != 0 ||
            before.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            before.TxOccupancy.FreshUntil <= m_timeProvider.GetUtcNow() ||
            before.TxOccupancy.Occupants.Count != 0)
        {
            throw new InvalidOperationException(
                "PSOC2 idle defaults may be restored only with fresh idle, zero TX occupants, and no external GUI client.");
        }
        if (before.TransmitSettings is null)
        {
            throw new InvalidOperationException(
                "PSOC2 did not provide a complete transmit-settings snapshot.");
        }

        HilTransmitSettings target = new(
            RfPower: 100,
            DaxEnabled: true,
            MicSelection: "PC",
            VoxEnabled: false);
        await session.RestoreTransmitSettingsAsync(target, cancellationToken);
        HilRadioSnapshot after = session.Snapshot();
        if (after.TransmitSettings != target ||
            after.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            after.TxOccupancy.Occupants.Count != 0)
        {
            throw new InvalidOperationException(
                "PSOC2 did not confirm the expected idle station defaults after restoration.");
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            operation = "restore-idle-defaults",
            passed = true,
            radio = options.RadioId,
            serial = after.Serial,
            before = before.TransmitSettings,
            after = after.TransmitSettings,
            txState = after.TxOccupancy.StateName,
            txOccupants = after.TxOccupancy.Occupants.Count,
            rfEmitted = false,
            keyCommandIssued = false,
            unkeyCommandIssued = false
        }, JsonOptions));
        return 0;
    }

    private async Task<HilFlexSession> ConnectRecoveryGuiWithRetriesAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        Exception? lastException = null;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            HilFlexSession session = NewSession(options.RadioId);
            try
            {
                await session.ConnectAsync(
                    options.Host,
                    options.Port,
                    registerGui: true,
                    cancellationToken);
                return session;
            }
            catch (Exception exception) when (
                exception is TimeoutException or IOException)
            {
                lastException = exception;
                await session.DisposeAsync();
                if (attempt == maximumAttempts)
                {
                    break;
                }
                m_loggerFactory
                    .CreateLogger<HilRunner>()
                    .LogWarning(
                        exception,
                        "Transient FLEX connection failure while restoring idle defaults; retrying with a fresh session ({Attempt}/{MaximumAttempts})",
                        attempt,
                        maximumAttempts);
                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    m_timeProvider,
                    cancellationToken);
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        throw new TimeoutException(
            $"Could not establish a fresh FLEX GUI session for idle-default recovery after {maximumAttempts} attempts.",
            lastException);
    }

    private async Task<int> VerifyExternalBlockAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession session = NewSession(options.RadioId);
        await session.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: true,
            cancellationToken);
        HilRadioSnapshot snapshot = session.Snapshot();
        VerifyIdentity(snapshot, options.ExpectedSerial);

        if (snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle)
        {
            throw new InvalidOperationException(
                "The external-block test requires an idle interlock.");
        }
        RadioTxOccupant externalPttOwner = snapshot.TxOccupancy.LocalPttOwners
            .SingleOrDefault(owner => !owner.AetherOwned) ??
            throw new InvalidOperationException(
                "No external Local PTT owner is present. Connect SmartSDR and make it the Local PTT owner before this test.");

        const string sessionId = "tx-hil-external-block";
        const string browserClientId = "tx-hil-browser";
        TxLeaseManager leases = new(m_timeProvider);
        if (!leases.TryAcquire(
                options.RadioId,
                sessionId,
                browserClientId,
                "tx-hil",
                "HIL External Block",
                TimeSpan.FromSeconds(5),
                out TxLease? lease,
                out string? leaseError))
        {
            throw new InvalidOperationException(
                leaseError ?? "Could not acquire the local HIL test lease.");
        }

        NoCommandTransport transport = new(session.ClientHandle);
        await using StationTxCommandGate gate = new(
            allowTransmit: true,
            options.RadioId,
            leases,
            session.OccupancyRegistry,
            transport,
            m_timeProvider);
        StationTxGateResult result = await gate.RequestKeyAsync(
            lease!.LeaseId,
            sessionId,
            browserClientId,
            cancellationToken);

        if (result.Success ||
            result.Code != "external_local_ptt_owner" ||
            transport.CommandCount != 0)
        {
            throw new InvalidOperationException(
                "The live external-owner denial did not fail closed before the command transport.");
        }

        leases.TryRelease(
            options.RadioId,
            lease.LeaseId,
            sessionId,
            browserClientId,
            "hil-test-complete",
            out _);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            test = "external-local-ptt-block",
            passed = true,
            denial = result.Code,
            commandCount = transport.CommandCount,
            externalOwner = new
            {
                externalPttOwner.Program,
                externalPttOwner.Station,
                handle = $"0x{externalPttOwner.ClientHandle:x8}"
            },
            txOccupancy = snapshot.TxOccupancy.StateName
        }, JsonOptions));
        return 0;
    }

    private async Task<int> VerifyCwxConfigurationAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession session = NewSession(options.RadioId);
        await session.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: true,
            cancellationToken);
        HilRadioSnapshot snapshot = session.Snapshot();
        VerifyIdentity(snapshot, options.ExpectedSerial);
        if (snapshot.ExternalGuiClients.Count != 0)
        {
            throw new InvalidOperationException(
                "CWX configuration verification requires every external FLEX GUI client to be disconnected.");
        }
        if (snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= m_timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException(
                "CWX configuration verification requires a fresh idle interlock.");
        }

        HilCwxIdentifier identifier = new(m_timeProvider);
        HilCwxConfigurationRoundTripResult result =
            await identifier.VerifyConfigurationRoundTripAsync(
                session,
                cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            test = "cwx-configuration-round-trip",
            passed = true,
            radio = options.RadioId,
            serial = snapshot.Serial,
            clientHandle = $"0x{session.ClientHandle:x8}",
            original = new
            {
                result.OriginalWpm,
                result.OriginalBreakInDelayMilliseconds,
                result.OriginalQskEnabled
            },
            temporary = new
            {
                result.TestWpm,
                result.TestBreakInDelayMilliseconds,
                result.TestQskEnabled
            },
            commandsExcluded = new[]
            {
                "cwx send",
                "xmit 1"
            }
        }, JsonOptions));
        return 0;
    }

    private async Task<int> VerifyPreflightAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession session = NewSession(options.RadioId);
        await session.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: true,
            cancellationToken);
        HilRadioSnapshot initial = session.Snapshot();
        VerifyIdentity(initial, options.ExpectedSerial);
        if (initial.ExternalGuiClients.Count != 0)
        {
            throw new InvalidOperationException(
                "Preflight requires every external FLEX GUI client to be disconnected.");
        }
        if (initial.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            initial.TxOccupancy.FreshUntil <= m_timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException(
                "Preflight requires a fresh idle interlock.");
        }
        HilTransmitSettings previousTransmitSettings =
            initial.TransmitSettings ??
            throw new InvalidOperationException(
                "Preflight requires a complete restorable transmit-route snapshot.");
        if (!initial.Cwx.HasFreshConfiguration(m_timeProvider.GetUtcNow()))
        {
            throw new InvalidOperationException(
                "Preflight requires fresh restorable CWX status.");
        }

        HilOwnedRadioResources? resources = null;
        try
        {
            resources = await session.CreateOwnedTxResourcesAsync(
                options,
                cancellationToken);
            HilTransmitSettings captured =
                await session.ConfigureSilentTransmitAsync(cancellationToken);
            if (captured != previousTransmitSettings)
            {
                throw new InvalidOperationException(
                    "Transmit settings changed during preflight staging.");
            }
            await session.RequestLocalPttAsync(cancellationToken);
            HilRadioSnapshot armed = session.Snapshot();
            if (armed.TxOccupancy.State != RadioTxOccupancyState.Idle ||
                !armed.TxOccupancy.HasExclusiveLocalPttAuthority(
                    session.ClientHandle))
            {
                throw new InvalidOperationException(
                    "Preflight did not confirm exclusive HIL Local PTT authority while idle.");
            }
            await session.SetOwnedSliceModeAsync(
                resources,
                "CW",
                cancellationToken);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                test = "full-no-rf-preflight",
                passed = true,
                radio = options.RadioId,
                serial = initial.Serial,
                clientHandle = $"0x{session.ClientHandle:x8}",
                staged = new
                {
                    resources.PanId,
                    resources.WaterfallId,
                    resources.SliceId,
                    options.FrequencyHz,
                    txAntenna = options.TxAntenna,
                    initialMode = options.Mode,
                    identificationMode = "CW",
                    rfPower = options.RfPower,
                    localPtt = "exclusive-hil",
                    interlock = "idle"
                },
                commandsExcluded = new[]
                {
                    "xmit 1",
                    "cwx send"
                }
            }, JsonOptions));
            return 0;
        }
        finally
        {
            using CancellationTokenSource cleanup =
                new(TimeSpan.FromSeconds(15));
            bool radioIdle = await ConfirmRadioIdleAsync(
                session,
                TimeSpan.FromSeconds(5),
                cleanup.Token);
            if (!radioIdle)
            {
                m_loggerFactory.CreateLogger<HilRunner>().LogCritical(
                    "No-RF preflight lost fresh idle confirmation; verify PSOC2 locally before any further test");
            }
            else
            {
                try
                {
                    await session.RestoreTransmitSettingsAsync(
                        previousTransmitSettings,
                        cleanup.Token);
                }
                catch (Exception exception)
                {
                    m_loggerFactory.CreateLogger<HilRunner>().LogError(
                        exception,
                        "No-RF preflight could not restore transmit settings {TransmitSettings}",
                        previousTransmitSettings);
                }
                if (resources is not null)
                {
                    await session.RemoveOwnedTxResourcesAsync(
                        resources,
                        cleanup.Token);
                }
            }
        }
    }

    private async Task<int> PrepareAsync(
        HilOptions options,
        string purpose,
        string operationLabel,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession session = NewSession(options.RadioId);
        await session.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: true,
            cancellationToken);
        HilRadioSnapshot snapshot = session.Snapshot();
        VerifyIdentity(snapshot, options.ExpectedSerial);
        if (snapshot.ExternalGuiClients.Count != 0)
        {
            throw new InvalidOperationException(
                "Every external FLEX GUI client must be disconnected before an arm manifest can be prepared.");
        }
        if (snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= m_timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException(
                "PSOC2 must be radio-authoritatively idle before an arm manifest can be prepared.");
        }
        if (snapshot.TransmitSettings is null)
        {
            throw new InvalidOperationException(
                "PSOC2 did not provide a complete restorable transmit-route snapshot.");
        }
        if (!snapshot.Cwx.HasFreshConfiguration(m_timeProvider.GetUtcNow()))
        {
            throw new InvalidOperationException(
                "PSOC2 did not provide fresh restorable CWX WPM, QSK, and break-in-delay status.");
        }

        (HilArmManifest manifest, string token) =
            HilArmManifest.Create(options, m_timeProvider, purpose);
        await HilArmManifest.WriteAsync(
            options.ArmFile,
            manifest,
            cancellationToken);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            prepared = true,
            purpose = manifest.Purpose,
            operation = operationLabel,
            armFile = options.ArmFile,
            expiresAt = manifest.ExpiresAt,
            oneTimeToken = token,
            radio = new
            {
                snapshot.Model,
                snapshot.Serial,
                options.Host,
                options.TxAntenna,
                options.FrequencyHz,
                options.Mode,
                options.RfPower,
                options.KeyMilliseconds
            },
            safetyExpiry = purpose == HilArmManifest.SafetyExpiryPurpose
                ? new
                {
                    heartbeatExpiryMilliseconds =
                        HilSafetyExpiryOperation
                            .ExpiringHeartbeatTimeout
                            .TotalMilliseconds,
                    engineExplicitUnkey = false,
                    independentObserverUnkeyOnly = true
                }
                : null,
            safetySessionLoss =
                purpose == HilArmManifest.SafetySessionLossPurpose
                    ? new
                    {
                        controllingSessionLeaseReleased = true,
                        explicitSupervisorAbort = "browser-session-lost",
                        engineExplicitUnkey = false,
                        independentObserverUnkeyOnly = true
                    }
                    : null,
            safetyEngineConnectionLoss =
                purpose == HilArmManifest.SafetyEngineConnectionLossPurpose
                    ? new
                    {
                        injectedBoundary =
                            "station-engine-tx-command-channel",
                        exactConnectedToDisconnectedTransition = true,
                        engineExplicitUnkey = false,
                        independentObserverUnkeyOnly = true,
                        statusSessionRetainedForEvidenceAndCleanup = true,
                        fullProcessKill = false
                    }
                    : null,
            safetyProcessLoss =
                purpose == HilArmManifest.SafetyProcessLossPurpose
                    ? new
                    {
                        injectedBoundary = "engine-process-and-flex-tcp",
                        childPlanLifetimeSeconds =
                            HilEngineProcessChildPlan.Lifetime.TotalSeconds,
                        exactRosterConnectedToAbsentTransition = true,
                        engineExplicitUnkey = false,
                        independentObserverUnkeyOnly = true,
                        processKillEntireTree = true,
                        gracefulChildCleanupExpected = false
                    }
                    : null,
            externalGuiClients = snapshot.ExternalGuiClients.Select(ClientView),
            cwx = new
            {
                snapshot.Cwx.Wpm,
                snapshot.Cwx.QskEnabled,
                snapshot.Cwx.BreakInDelayMilliseconds,
                snapshot.Cwx.ConfigurationObservedAt,
                snapshot.Cwx.ConfigurationFreshUntil
            },
            warning =
                "the armed operation will consume this manifest before connecting; every external GUI client must be disconnected, the camera must remain live, and remote power control must remain immediately available"
        }, JsonOptions));
        return 0;
    }

    private Task<int> VerifySafetyExpiryPreflightAsync(
        HilOptions options,
        CancellationToken cancellationToken) =>
        new HilSafetyExpiryOperation(
            m_loggerFactory,
            m_timeProvider).VerifyPreflightAsync(
                options,
                cancellationToken);

    private Task<int> SafetyExpiryAsync(
        HilOptions commandLine,
        CancellationToken cancellationToken) =>
        new HilSafetyExpiryOperation(
            m_loggerFactory,
            m_timeProvider).RunAsync(
                commandLine,
                cancellationToken);

    private Task<int> VerifySafetySessionLossPreflightAsync(
        HilOptions options,
        CancellationToken cancellationToken) =>
        new HilSafetySessionLossOperation(
            m_loggerFactory,
            m_timeProvider).VerifyPreflightAsync(
                options,
                cancellationToken);

    private Task<int> SafetySessionLossAsync(
        HilOptions commandLine,
        CancellationToken cancellationToken) =>
        new HilSafetySessionLossOperation(
            m_loggerFactory,
            m_timeProvider).RunAsync(
                commandLine,
                cancellationToken);

    private Task<int> VerifySafetyEngineConnectionLossPreflightAsync(
        HilOptions options,
        CancellationToken cancellationToken) =>
        new HilSafetyEngineConnectionLossOperation(
            m_loggerFactory,
            m_timeProvider).VerifyPreflightAsync(
                options,
                cancellationToken);

    private Task<int> SafetyEngineConnectionLossAsync(
        HilOptions commandLine,
        CancellationToken cancellationToken) =>
        new HilSafetyEngineConnectionLossOperation(
            m_loggerFactory,
            m_timeProvider).RunAsync(
                commandLine,
                cancellationToken);

    private Task<int> VerifySafetyProcessLossPreflightAsync(
        HilOptions options,
        CancellationToken cancellationToken) =>
        new HilSafetyEngineProcessLossOperation(
            m_loggerFactory,
            m_timeProvider).VerifyPreflightAsync(
                options,
                cancellationToken);

    private Task<int> SafetyProcessLossAsync(
        HilOptions commandLine,
        CancellationToken cancellationToken) =>
        new HilSafetyEngineProcessLossOperation(
            m_loggerFactory,
            m_timeProvider).RunAsync(
                commandLine,
                cancellationToken);

    private async Task<int> PulseAsync(
        HilOptions commandLine,
        CancellationToken cancellationToken)
    {
        HilArmManifest manifest = await HilArmManifest.ConsumeAsync(
            commandLine.ArmFile,
            commandLine.Token,
            m_timeProvider,
            cancellationToken);
        HilOptions options = HilArmManifest.ToPulseOptions(
            manifest,
            commandLine.ArmFile,
            commandLine.Token);

        await using HilFlexSession session = NewSession(options.RadioId);
        await session.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: true,
            cancellationToken);
        HilRadioSnapshot initial = session.Snapshot();
        VerifyIdentity(initial, options.ExpectedSerial);
        if (initial.ExternalGuiClients.Count != 0)
        {
            throw new InvalidOperationException(
                "The one-time arm manifest was consumed, but external GUI clients are still connected. No TX command was sent; prepare a new manifest after disconnecting them.");
        }
        if (initial.TxOccupancy.State != RadioTxOccupancyState.Idle)
        {
            throw new InvalidOperationException(
                "The radio interlock is not idle. No TX command was sent.");
        }
        HilTransmitSettings previousTransmitSettings =
            initial.TransmitSettings ??
            throw new InvalidOperationException(
                "The radio did not report a complete transmit route snapshot, so the on-air HIL pulse cannot restore it safely.");
        if (!initial.Cwx.HasFreshConfiguration(m_timeProvider.GetUtcNow()))
        {
            throw new InvalidOperationException(
                "The radio did not report fresh restorable CWX WPM, QSK, and break-in-delay status. No TX command was sent.");
        }

        HilOwnedRadioResources? resources = null;
        StationTxCommandGate? gate = null;
        TxLeaseManager? leases = null;
        TxLease? lease = null;
        const string sessionId = "tx-hil-pulse";
        const string browserClientId = "tx-hil-pulse-owner";
        try
        {
            resources = await session.CreateOwnedTxResourcesAsync(
                options,
                cancellationToken);
            HilTransmitSettings capturedSettings =
                await session.ConfigureSilentTransmitAsync(cancellationToken);
            if (capturedSettings != previousTransmitSettings)
            {
                throw new InvalidOperationException(
                    "The transmit settings changed after the initial safety snapshot; no TX command was sent.");
            }
            await session.RequestLocalPttAsync(cancellationToken);

            HilRadioSnapshot armed = session.Snapshot();
            if (!armed.TxOccupancy.HasExclusiveLocalPttAuthority(
                    session.ClientHandle) ||
                armed.TxOccupancy.State != RadioTxOccupancyState.Idle)
            {
                throw new InvalidOperationException(
                    "PSOC2 did not confirm exclusive HIL Local PTT authority while idle.");
            }

            leases = new TxLeaseManager(m_timeProvider);
            if (!leases.TryAcquire(
                    options.RadioId,
                    sessionId,
                    browserClientId,
                    "tx-hil",
                    "PSOC2 HIL Pulse",
                    TimeSpan.FromSeconds(15),
                    out lease,
                    out string? leaseError))
            {
                throw new InvalidOperationException(
                    leaseError ?? "Could not acquire the HIL TX lease.");
            }

            gate = new StationTxCommandGate(
                allowTransmit: true,
                options.RadioId,
                leases,
                session.OccupancyRegistry,
                resources.Transport,
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
            DateTimeOffset keyedAt = m_timeProvider.GetUtcNow();
            await Task.Delay(
                TimeSpan.FromMilliseconds(options.KeyMilliseconds),
                cancellationToken);

            StationTxGateResult unkey = await gate.RequestUnkeyAsync(
                lease.LeaseId,
                sessionId,
                browserClientId,
                cancellationToken);
            if (!unkey.Success &&
                unkey.Snapshot.State != StationTxGateState.UnkeyPending)
            {
                throw new InvalidOperationException(
                    $"The unkey request failed: {unkey.Code}: {unkey.Message}");
            }

            StationTxGateResult idle = await WaitForGateStateAsync(
                gate,
                StationTxGateState.Idle,
                TimeSpan.FromSeconds(3),
                cancellationToken);
            DateTimeOffset idleAt = m_timeProvider.GetUtcNow();

            await session.SetOwnedSliceModeAsync(
                resources,
                "CW",
                cancellationToken);
            HilCwxIdentifier cwxIdentifier = new(m_timeProvider);
            HilCwxIdentificationResult identification =
                await cwxIdentifier.IdentifyAsync(
                    session,
                    cancellationToken);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                pulse = "passed",
                radio = options.RadioId,
                serial = initial.Serial,
                clientHandle = $"0x{session.ClientHandle:x8}",
                resources = new
                {
                    resources.PanId,
                    resources.WaterfallId,
                    resources.SliceId,
                    options.TxAntenna,
                    options.FrequencyHz,
                    options.Mode,
                    options.RfPower,
                    silentRoute = new
                    {
                        dax = true,
                        micSelection = "PC",
                        vox = false,
                        txAudioStreamCreated = false
                    }
                },
                lease = new
                {
                    lease.AcquiredAt,
                    lease.ExpiresAt
                },
                key = new
                {
                    keyedAt,
                    state = keyed.Snapshot.State.ToString(),
                    keyed.Snapshot.Reason
                },
                unkey = new
                {
                    idleAt,
                    state = idle.Snapshot.State.ToString(),
                    idle.Snapshot.Reason,
                    measuredKeyMilliseconds =
                        (idleAt - keyedAt).TotalMilliseconds
                },
                identification = new
                {
                    identification.Callsign,
                    identification.Wpm,
                    identification.StartIndex,
                    identification.EndIndex,
                    identification.StartedAt,
                    identification.DrainedAt,
                    identification.IdleAt,
                    identification.SawExactOwnedTransmit,
                    mode = "CW"
                }
            }, JsonOptions));
            return 0;
        }
        finally
        {
            using CancellationTokenSource cleanup =
                new(TimeSpan.FromSeconds(15));
            bool gateIdle = true;
            if (gate is not null)
            {
                gateIdle = await EmergencyUnkeyAsync(
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
                    "hil-cleanup",
                    out _);
            }
            bool radioIdle = gateIdle &&
                await ConfirmRadioIdleAsync(
                    session,
                    TimeSpan.FromSeconds(5),
                    cleanup.Token);
            if (!radioIdle)
            {
                m_loggerFactory.CreateLogger<HilRunner>().LogCritical(
                    "PSOC2 did not provide fresh idle confirmation. RF power remains at the bounded HIL setting and owned radio resources were not deliberately removed; verify and unkey PSOC2 locally immediately");
            }
            else
            {
                try
                {
                    await session.RestoreTransmitSettingsAsync(
                        previousTransmitSettings,
                        cleanup.Token);
                }
                catch (Exception exception)
                {
                    m_loggerFactory.CreateLogger<HilRunner>().LogError(
                        exception,
                        "Could not restore the previous transmit settings {TransmitSettings}",
                        previousTransmitSettings);
                }
                if (resources is not null)
                {
                    await session.RemoveOwnedTxResourcesAsync(
                        resources,
                        cleanup.Token);
                }
            }
        }
    }

    private async Task<bool> EmergencyUnkeyAsync(
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
            m_loggerFactory.CreateLogger<HilRunner>().LogCritical(
                exception,
                "Emergency HIL unkey did not receive idle confirmation; verify PSOC2 locally immediately");
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
                    snapshot.TxOccupancy.State == RadioTxOccupancyState.Idle &&
                    snapshot.TxOccupancy.ObservedAt is not null &&
                    snapshot.TxOccupancy.FreshUntil > DateTimeOffset.UtcNow,
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

    private static async Task<StationTxGateResult> WaitForGateStateAsync(
        StationTxCommandGate gate,
        StationTxGateState expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        StationTxGateResult last = new(
            false,
            "not-evaluated",
            string.Empty,
            gate.Snapshot);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await gate.EvaluateAsync(
                "hil-watchdog",
                cancellationToken);
            if (last.Snapshot.State == expected)
            {
                return last;
            }
            if (last.Snapshot.State == StationTxGateState.Faulted)
            {
                throw new InvalidOperationException(
                    $"The TX gate faulted: {last.Code}: {last.Message}");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        throw new TimeoutException(
            $"The TX gate did not reach {expected}; last state was " +
            $"{last.Snapshot.State} ({last.Code}: {last.Message}).");
    }

    private HilFlexSession NewSession(string radioId) =>
        new(
            radioId,
            m_loggerFactory.CreateLogger<HilFlexSession>());

    private static void VerifyIdentity(
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
    }

    private static void WriteSnapshot(
        string operation,
        HilRadioSnapshot snapshot)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            operation,
            radio = new
            {
                snapshot.Model,
                snapshot.Serial,
                clientHandle = $"0x{snapshot.ClientHandle:x8}",
                snapshot.RfPower,
                snapshot.TransmitSettings,
                cwx = snapshot.Cwx
            },
            tx = new
            {
                state = snapshot.TxOccupancy.StateName,
                snapshot.TxOccupancy.ObservedAt,
                snapshot.TxOccupancy.FreshUntil,
                localPttOwners = snapshot.TxOccupancy.LocalPttOwners.Select(
                    OwnerView),
                occupants = snapshot.TxOccupancy.Occupants.Select(OwnerView)
            },
            guiClients = snapshot.GuiClients.Select(ClientView),
            slices = snapshot.Slices
                .OrderBy(slice => slice.Key)
                .Select(slice => new
                {
                    id = slice.Key,
                    fields = slice.Value
                })
        }, JsonOptions));
    }

    private static object ClientView(RadioGuiClientDiagnostics client) =>
        new
        {
            handle = $"0x{client.ClientHandle:x8}",
            client.Program,
            client.Station,
            client.Source,
            client.LocalPtt,
            client.IsThisSession
        };

    private static object OwnerView(RadioTxOccupant owner) =>
        new
        {
            handle = $"0x{owner.ClientHandle:x8}",
            owner.Program,
            owner.Station,
            owner.Source,
            owner.AetherOwned
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

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

    private sealed class NoCommandTransport(uint clientHandle)
        : IStationTxCommandTransport
    {
        public bool IsConnected => true;
        public uint ClientHandle { get; } = clientHandle;
        public int CommandCount { get; private set; }

        public Task<StationTxTransportResult> SetTransmitAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            CommandCount++;
            throw new InvalidOperationException(
                "The external-owner denial reached the TX command transport.");
        }
    }
}
