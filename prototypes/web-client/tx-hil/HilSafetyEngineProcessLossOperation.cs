using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging;

namespace AetherSDR.TxHil;

internal sealed class HilSafetyEngineProcessLossOperation(
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan SafetyHeartbeatTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan CleanupConnectRetryDelay =
        TimeSpan.FromSeconds(2);
    private const int CleanupConnectAttempts = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ILoggerFactory m_loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly TimeProvider m_timeProvider =
        timeProvider ?? TimeProvider.System;

    public Task<int> VerifyPreflightAsync(
        HilOptions options,
        CancellationToken cancellationToken) =>
        RunCoreAsync(options, emitRf: false, cancellationToken);

    public async Task<int> RunAsync(
        HilOptions commandLine,
        CancellationToken cancellationToken)
    {
        HilArmManifest manifest = await HilArmManifest.ConsumeAsync(
            commandLine.ArmFile,
            commandLine.Token,
            HilArmManifest.SafetyProcessLossPurpose,
            m_timeProvider,
            cancellationToken);
        HilOptions options = HilArmManifest.ToSafetyProcessLossOptions(
            manifest,
            commandLine.ArmFile,
            commandLine.Token);
        return await RunCoreAsync(options, emitRf: true, cancellationToken);
    }

    private async Task<int> RunCoreAsync(
        HilOptions options,
        bool emitRf,
        CancellationToken cancellationToken)
    {
        HilRadioSnapshot initial;
        await using (HilFlexSession inspector = NewSession(options.RadioId))
        {
            await inspector.ConnectAsync(
                options.Host,
                options.Port,
                registerGui: true,
                cancellationToken);
            initial = inspector.Snapshot();
            VerifyInitialState(initial, options.ExpectedSerial);
        }

        await using HilFlexSession observer = NewSession(options.RadioId);
        await observer.ConnectAsync(
            options.Host,
            options.Port,
            registerGui: false,
            cancellationToken);
        VerifyObserverState(observer.Snapshot(), options.ExpectedSerial);
        HilTransmitSettings previousTransmitSettings = initial.TransmitSettings!;

        string childPlanFile = Path.Combine(
            Path.GetTempPath(),
            $"aethersdr-engine-child-{Guid.NewGuid():N}.json");
        using Process parent = Process.GetCurrentProcess();
        (HilEngineProcessChildPlan childPlan, string childToken) =
            HilEngineProcessChildPlan.Create(
                options,
                parent,
                m_timeProvider);
        await HilEngineProcessChildPlan.WriteAsync(
            childPlanFile,
            childPlan,
            cancellationToken);

        Process? child = null;
        Task<string>? childErrors = null;
        HilEngineChildReady? ready = null;
        HilEngineChildKeyed? keyed = null;
        StationTxSafetySupervisor? supervisor = null;
        StationTxEngineConnectionMonitor? connectionMonitor = null;
        CountingEmergencyUnkeyTransport? safetyTransport = null;
        DateTimeOffset? processKillRequestedAt = null;
        DateTimeOffset? processExitedAt = null;
        DateTimeOffset? rosterLossObservedAt = null;
        DateTimeOffset? safetySignalAt = null;
        DateTimeOffset? safetyActionAt = null;
        DateTimeOffset? idleAt = null;
        HilCwxIdentificationResult? identification = null;
        HilEngineRestartReconciliation? replacementEngine = null;
        bool childResourcesGoneBeforeCleanup = false;
        bool childKilled = false;
        int? childExitCode = null;

        try
        {
            (child, childErrors) = StartChild(childPlanFile, childToken);
            ready = await ReadChildMessageAsync<HilEngineChildReady>(
                child,
                childErrors,
                "ready",
                TimeSpan.FromSeconds(10),
                cancellationToken);
            VerifyReady(ready, childPlan, child);

            await WaitForObserverOwnershipAsync(
                observer,
                ready.ClientHandle,
                cancellationToken);

            safetyTransport = new CountingEmergencyUnkeyTransport(
                new HilEmergencyUnkeyTransport(observer),
                m_timeProvider);
            supervisor = new StationTxSafetySupervisor(
                options.RadioId,
                observer.OccupancyRegistry,
                safetyTransport,
                m_timeProvider);
            StationTxSafetyArm safetyArm = new(
                ready.EngineInstanceId,
                ready.LeaseId,
                ready.SessionId,
                ready.BrowserClientId,
                ready.ClientHandle,
                SafetyHeartbeatTimeout);
            RequireSuccess(
                await supervisor.ArmAsync(safetyArm, cancellationToken),
                "arm the independent process-loss supervisor");

            connectionMonitor = new StationTxEngineConnectionMonitor(supervisor);
            RequireConnectionSuccess(
                await connectionMonitor.EvaluateAsync(
                    Observation(ready, isConnected: true),
                    cancellationToken),
                "observe the exact child engine process connection");

            if (emitRf)
            {
                await child.StandardInput.WriteLineAsync("key");
                await child.StandardInput.FlushAsync(cancellationToken);
                keyed = await ReadChildMessageAsync<HilEngineChildKeyed>(
                    child,
                    childErrors,
                    "keyed",
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                if (keyed.ClientHandle != ready.ClientHandle ||
                    keyed.ProcessId != ready.ProcessId ||
                    keyed.EngineCommands.Key != 1 ||
                    keyed.EngineCommands.Unkey != 0)
                {
                    throw new InvalidOperationException(
                        "The child keyed evidence did not match the exact ready process and handle.");
                }

                StationTxSafetyResult protectedTx = await WaitForSafetyAsync(
                    supervisor,
                    result =>
                        result.Code == "protected_tx" &&
                        result.Snapshot.SawProtectedTransmit,
                    "observer confirmation of the exact child TX handle",
                    TimeSpan.FromSeconds(3),
                    cancellationToken);
                RequireSuccess(protectedTx, "confirm exact child transmit ownership");
            }

            processKillRequestedAt = m_timeProvider.GetUtcNow();
            child.Kill(entireProcessTree: true);
            childKilled = true;
            await child.WaitForExitAsync(cancellationToken);
            processExitedAt = m_timeProvider.GetUtcNow();
            childExitCode = child.ExitCode;

            safetySignalAt = m_timeProvider.GetUtcNow();
            StationTxEngineConnectionResult loss =
                await connectionMonitor.EvaluateAsync(
                    Observation(ready, isConnected: false),
                    cancellationToken);
            if (!loss.LossSignaled ||
                (!loss.Success &&
                 loss.Code != "emergency_unkey_outcome_unknown"))
            {
                throw new InvalidOperationException(
                    $"The true process-loss monitor failed closed: {loss.Code}: {loss.Message}");
            }
            safetyActionAt = m_timeProvider.GetUtcNow();

            StationTxSafetyResult safetyIdle = await WaitForSafetyAsync(
                supervisor,
                result => result.Snapshot.State == StationTxSafetyState.Disarmed,
                "independent observer to confirm post-process-loss idle",
                TimeSpan.FromSeconds(5),
                cancellationToken);
            RequireSuccess(safetyIdle, "confirm post-process-loss idle");
            await ConfirmRadioIdleOrThrowAsync(
                observer,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            idleAt = m_timeProvider.GetUtcNow();

            await observer.WaitForAsync(
                snapshot =>
                    !snapshot.GuiClients.Any(client =>
                        client.ClientHandle == ready.ClientHandle) &&
                    snapshot.TxOccupancy.FreshUntil > m_timeProvider.GetUtcNow(),
                TimeSpan.FromSeconds(5),
                cancellationToken);
            rosterLossObservedAt = m_timeProvider.GetUtcNow();

            if (safetyTransport.CommandCount is < 0 or > 1)
            {
                throw new InvalidOperationException(
                    "The process-loss observer may issue at most one unkey command.");
            }
            if (!emitRf && safetyTransport.CommandCount != 0)
            {
                throw new InvalidOperationException(
                    "The no-RF process-loss preflight issued an unexpected unkey command while idle.");
            }

            childResourcesGoneBeforeCleanup =
                await WaitForFrequencyResourcesGoneAsync(
                    observer,
                    options.FrequencyHz,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            if (!childResourcesGoneBeforeCleanup)
            {
                throw new InvalidOperationException(
                    "The killed engine process left its test-frequency slice present.");
            }

            await RestoreOnlyAsync(
                options,
                previousTransmitSettings,
                cancellationToken);
            replacementEngine = await VerifyReplacementEngineAsync(
                options,
                ready,
                observer,
                parent,
                previousTransmitSettings,
                cancellationToken);

            if (emitRf)
            {
                identification = await IdentifyAndRestoreAsync(
                    options,
                    previousTransmitSettings,
                    cancellationToken);
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                test = emitRf
                    ? "independent-engine-process-loss-unkey"
                    : "independent-engine-process-loss-no-rf-preflight",
                passed = true,
                radio = options.RadioId,
                serial = initial.Serial,
                frequencyHz = options.FrequencyHz,
                txAntenna = options.TxAntenna,
                rfPower = options.RfPower,
                rfEmitted = emitRf,
                childProcess = new
                {
                    ready.ProcessId,
                    ready.EngineInstanceId,
                    ready.SessionId,
                    ready.BrowserClientId,
                    ready.LeaseId,
                    clientHandle = $"0x{ready.ClientHandle:x8}",
                    killed = childKilled,
                    entireProcessTree = true,
                    exitCode = childExitCode,
                    gracefulCleanupRan = false,
                    childResourcesGoneBeforeCleanup
                },
                independentObserver = new
                {
                    clientHandle = $"0x{observer.ClientHandle:x8}",
                    guiRegistered = observer.GuiRegistered,
                    unkeyCommands = safetyTransport.CommandCount,
                    keyCapability = false,
                    mechanism = emitRf
                        ? safetyTransport.CommandCount == 0
                            ? "radio-auto-unkey-on-engine-tcp-close"
                            : "independent-observer-unkey"
                        : "idle-disarm"
                },
                childCommandsBeforeKill = keyed is null
                    ? new { key = 0, unkey = 0 }
                    : new
                    {
                        key = keyed.EngineCommands.Key,
                        unkey = keyed.EngineCommands.Unkey
                    },
                replacementEngine = new
                {
                    replacementEngine.ProcessId,
                    replacementEngine.EngineInstanceId,
                    replacementEngine.SessionId,
                    replacementEngine.BrowserClientId,
                    replacementEngine.LeaseId,
                    clientHandle =
                        $"0x{replacementEngine.ClientHandle:x8}",
                    replacementEngine.ExitCode,
                    replacementEngine.OldHandleAbsent,
                    replacementEngine.AllIdentitiesFresh,
                    replacementEngine.ResourcesGone,
                    replacementEngine.BaselineRestored,
                    replacementEngine.StartedAt,
                    replacementEngine.ReadyAt,
                    replacementEngine.ReconciledAt,
                    replacementEngine.ExitedAt,
                    commands = new
                    {
                        key = replacementEngine.EngineCommands.Key,
                        unkey = replacementEngine.EngineCommands.Unkey
                    }
                },
                timing = new
                {
                    keyedAt = keyed?.KeyedAt,
                    processKillRequestedAt,
                    processExitedAt,
                    rosterLossObservedAt,
                    safetySignalAt,
                    safetyActionAt,
                    unkeyDispatchedAt = safetyTransport.CommandDispatchedAt,
                    unkeyCompletedAt = safetyTransport.CommandCompletedAt,
                    idleAt,
                    processKillToExitMilliseconds =
                        (processExitedAt!.Value - processKillRequestedAt!.Value)
                            .TotalMilliseconds,
                    processExitToSafetySignalMilliseconds =
                        (safetySignalAt!.Value - processExitedAt.Value)
                            .TotalMilliseconds,
                    safetySignalToUnkeyDispatchMilliseconds =
                        safetyTransport.CommandDispatchedAt is null
                            ? (double?)null
                            : (safetyTransport.CommandDispatchedAt.Value -
                               safetySignalAt.Value).TotalMilliseconds,
                    unkeyDispatchToCompletionMilliseconds =
                        safetyTransport.CommandDispatchedAt is null ||
                        safetyTransport.CommandCompletedAt is null
                            ? (double?)null
                            : (safetyTransport.CommandCompletedAt.Value -
                               safetyTransport.CommandDispatchedAt.Value)
                                .TotalMilliseconds,
                    unkeyCompletionToIdleMilliseconds =
                        safetyTransport.CommandCompletedAt is null
                            ? (double?)null
                            : (idleAt!.Value -
                               safetyTransport.CommandCompletedAt.Value)
                                .TotalMilliseconds,
                    processExitToSafetyActionMilliseconds =
                        (safetyActionAt!.Value - processExitedAt.Value)
                            .TotalMilliseconds,
                    safetyActionToIdleMilliseconds =
                        (idleAt!.Value - safetyActionAt.Value)
                            .TotalMilliseconds,
                    processExitToRosterLossMilliseconds =
                        (rosterLossObservedAt!.Value - processExitedAt.Value)
                            .TotalMilliseconds,
                    idleToRosterLossMilliseconds =
                        (rosterLossObservedAt.Value - idleAt.Value)
                            .TotalMilliseconds,
                    keyedToIdleMilliseconds = keyed is null
                        ? (double?)null
                        : (idleAt!.Value - keyed.KeyedAt).TotalMilliseconds
                },
                identification = identification is null
                    ? null
                    : new
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

            if (child is not null && !child.HasExited)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(cleanup.Token);
                }
                catch (Exception exception)
                {
                    m_loggerFactory
                        .CreateLogger<HilSafetyEngineProcessLossOperation>()
                        .LogCritical(
                            exception,
                            "Could not terminate the HIL engine child process");
                }
            }

            if (supervisor is not null)
            {
                await EmergencySupervisorUnkeyAsync(
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

            if (File.Exists(childPlanFile))
            {
                try
                {
                    File.Delete(childPlanFile);
                }
                catch (Exception exception)
                {
                    m_loggerFactory
                        .CreateLogger<HilSafetyEngineProcessLossOperation>()
                        .LogWarning(
                            exception,
                            "Could not delete an unused child plan {ChildPlan}",
                            childPlanFile);
                }
            }

            child?.Dispose();
        }
    }

    private async Task<HilEngineRestartReconciliation>
        VerifyReplacementEngineAsync(
            HilOptions options,
            HilEngineChildReady previousEngine,
            HilFlexSession observer,
            Process parent,
            HilTransmitSettings expectedBaseline,
            CancellationToken cancellationToken)
    {
        string planFile = Path.Combine(
            Path.GetTempPath(),
            $"aethersdr-engine-restart-child-{Guid.NewGuid():N}.json");
        (HilEngineProcessChildPlan plan, string token) =
            HilEngineProcessChildPlan.Create(
                options,
                parent,
                m_timeProvider);
        await HilEngineProcessChildPlan.WriteAsync(
            planFile,
            plan,
            cancellationToken);

        Process? replacement = null;
        Task<string>? replacementErrors = null;
        DateTimeOffset startedAt = m_timeProvider.GetUtcNow();
        try
        {
            (replacement, replacementErrors) = StartChild(planFile, token);
            HilEngineChildReady ready =
                await ReadChildMessageAsync<HilEngineChildReady>(
                    replacement,
                    replacementErrors,
                    "ready",
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
            DateTimeOffset readyAt = m_timeProvider.GetUtcNow();
            VerifyReady(ready, plan, replacement);

            bool allIdentitiesFresh =
                ready.ProcessId != previousEngine.ProcessId &&
                ready.ClientHandle != previousEngine.ClientHandle &&
                !string.Equals(
                    ready.EngineInstanceId,
                    previousEngine.EngineInstanceId,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    ready.SessionId,
                    previousEngine.SessionId,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    ready.BrowserClientId,
                    previousEngine.BrowserClientId,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    ready.LeaseId,
                    previousEngine.LeaseId,
                    StringComparison.Ordinal);
            if (!allIdentitiesFresh)
            {
                throw new InvalidOperationException(
                    "The replacement engine reused stale process, session, lease, browser, or FLEX identity.");
            }

            await WaitForObserverOwnershipAsync(
                observer,
                ready.ClientHandle,
                cancellationToken);
            HilRadioSnapshot replacementObserved = observer.Snapshot();
            bool oldHandleAbsent =
                replacementObserved.GuiClients.All(client =>
                    client.ClientHandle != previousEngine.ClientHandle) &&
                replacementObserved.TxOccupancy.Occupants.All(occupant =>
                    occupant.ClientHandle != previousEngine.ClientHandle);
            if (!oldHandleAbsent ||
                replacementObserved.TxOccupancy.State !=
                    RadioTxOccupancyState.Idle ||
                replacementObserved.TxOccupancy.Occupants.Count != 0)
            {
                throw new InvalidOperationException(
                    "The replacement engine did not start from fresh radio idle with the old handle absent.");
            }

            await replacement.StandardInput.WriteLineAsync(
                "reconcile-idle-and-exit");
            await replacement.StandardInput.FlushAsync(cancellationToken);
            HilEngineChildIdleReconciled reconciled =
                await ReadChildMessageAsync<HilEngineChildIdleReconciled>(
                    replacement,
                    replacementErrors,
                    "idle-reconciled",
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            if (reconciled.ProcessId != ready.ProcessId ||
                reconciled.ClientHandle != ready.ClientHandle ||
                reconciled.ActiveIntent ||
                !string.Equals(
                    reconciled.GateState,
                    StationTxGateState.Idle.ToString(),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reconciled.TxState,
                    "idle",
                    StringComparison.OrdinalIgnoreCase) ||
                reconciled.EngineCommands.Key != 0 ||
                reconciled.EngineCommands.Unkey != 0)
            {
                throw new InvalidOperationException(
                    "The replacement engine did not reconcile and exit from exact zero-command idle.");
            }

            await replacement.WaitForExitAsync(cancellationToken);
            DateTimeOffset exitedAt = m_timeProvider.GetUtcNow();
            int exitCode = replacement.ExitCode;
            if (exitCode != 0)
            {
                string errors = await replacementErrors;
                throw new InvalidOperationException(
                    $"The replacement engine did not exit cleanly. Exit={exitCode}; stderr={errors}");
            }

            await observer.WaitForAsync(
                snapshot =>
                    snapshot.GuiClients.All(client =>
                        client.ClientHandle != ready.ClientHandle) &&
                    snapshot.TxOccupancy.State ==
                        RadioTxOccupancyState.Idle &&
                    snapshot.TxOccupancy.FreshUntil >
                        m_timeProvider.GetUtcNow(),
                TimeSpan.FromSeconds(5),
                cancellationToken);
            bool resourcesGone = await WaitForFrequencyResourcesGoneAsync(
                observer,
                options.FrequencyHz,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (!resourcesGone)
            {
                throw new InvalidOperationException(
                    "The replacement engine left its startup-reconciliation resources present.");
            }

            // The independent observer is no longer needed after both engine
            // handles and their owned resources are gone. Close it before the
            // final restore so the cleanup GUI is the only remaining FLEX
            // session while the radio settles the station-wide TX settings.
            await observer.DisposeAsync();

            await RestoreOnlyAsync(
                options,
                expectedBaseline,
                cancellationToken);
            await Task.Delay(
                TimeSpan.FromMilliseconds(500),
                m_timeProvider,
                cancellationToken);
            await VerifyBaselineAndNoFrequencyResourcesAsync(
                options,
                expectedBaseline,
                cancellationToken);

            return new HilEngineRestartReconciliation(
                ready.ProcessId,
                ready.EngineInstanceId,
                ready.SessionId,
                ready.BrowserClientId,
                ready.LeaseId,
                ready.ClientHandle,
                exitCode,
                oldHandleAbsent,
                allIdentitiesFresh,
                resourcesGone,
                BaselineRestored: true,
                startedAt,
                readyAt,
                reconciled.ReconciledAt,
                exitedAt,
                reconciled.EngineCommands);
        }
        finally
        {
            if (replacement is not null && !replacement.HasExited)
            {
                try
                {
                    replacement.Kill(entireProcessTree: true);
                    await replacement.WaitForExitAsync(cancellationToken);
                }
                catch
                {
                }
            }
            replacement?.Dispose();
            if (File.Exists(planFile))
            {
                try
                {
                    File.Delete(planFile);
                }
                catch
                {
                }
            }
        }
    }

    private async Task VerifyBaselineAndNoFrequencyResourcesAsync(
        HilOptions options,
        HilTransmitSettings expectedBaseline,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession verification =
            await ConnectCleanupGuiWithRetriesAsync(
                options,
                "replacement baseline verification",
                cancellationToken);
        HilRadioSnapshot snapshot = verification.Snapshot();
        VerifyCleanupState(snapshot, options.ExpectedSerial);
        string expectedMhz =
            (options.FrequencyHz / 1_000_000d).ToString(
                "0.000000",
                System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await verification.WaitForAsync(
                current =>
                    current.TransmitSettings == expectedBaseline &&
                    current.Slices.Values.All(fields =>
                        !fields.TryGetValue(
                            "RF_frequency",
                            out string? value) ||
                        !string.Equals(
                            value,
                            expectedMhz,
                            StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }
        catch (TimeoutException exception)
        {
            snapshot = verification.Snapshot();
            if (snapshot.TransmitSettings != expectedBaseline)
            {
                throw new InvalidOperationException(
                    $"The replacement engine did not restore the expected station baseline {expectedBaseline}; observed {snapshot.TransmitSettings}.",
                    exception);
            }
            throw new InvalidOperationException(
                "The replacement engine left a startup-reconciliation slice present.",
                exception);
        }

        VerifyCleanupState(verification.Snapshot(), options.ExpectedSerial);
    }

    private async Task<HilFlexSession> ConnectCleanupGuiWithRetriesAsync(
        HilOptions options,
        string operation,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= CleanupConnectAttempts; attempt++)
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
                if (attempt == CleanupConnectAttempts)
                {
                    break;
                }
                m_loggerFactory
                    .CreateLogger<HilSafetyEngineProcessLossOperation>()
                    .LogWarning(
                        exception,
                        "Transient FLEX connection failure during {Operation}; retrying with a fresh session ({Attempt}/{MaximumAttempts})",
                        operation,
                        attempt,
                        CleanupConnectAttempts);
                await Task.Delay(
                    CleanupConnectRetryDelay,
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
            $"Could not establish a fresh FLEX GUI session for {operation} after {CleanupConnectAttempts} attempts.",
            lastException);
    }

    private async Task<HilCwxIdentificationResult> IdentifyAndRestoreAsync(
        HilOptions options,
        HilTransmitSettings previousTransmitSettings,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession cleanup =
            await ConnectCleanupGuiWithRetriesAsync(
                options,
                "CW identification and restoration",
                cancellationToken);
        HilRadioSnapshot snapshot = cleanup.Snapshot();
        VerifyCleanupState(snapshot, options.ExpectedSerial);

        HilOwnedRadioResources? resources = null;
        try
        {
            resources = await cleanup.CreateOwnedTxResourcesAsync(
                options,
                cancellationToken);
            await cleanup.ConfigureSilentTransmitAsync(cancellationToken);
            await cleanup.RequestLocalPttAsync(cancellationToken);
            await cleanup.SetOwnedSliceModeAsync(
                resources,
                "CW",
                cancellationToken);
            HilCwxIdentifier identifier = new(m_timeProvider);
            HilCwxIdentificationResult identification =
                await identifier.IdentifyAsync(cleanup, cancellationToken);
            await cleanup.RestoreTransmitSettingsAsync(
                previousTransmitSettings,
                cancellationToken);
            await cleanup.RemoveOwnedTxResourcesAsync(
                resources,
                cancellationToken);
            return identification;
        }
        catch
        {
            HilRadioSnapshot failed = cleanup.Snapshot();
            if (failed.TxOccupancy.State == RadioTxOccupancyState.Idle)
            {
                try
                {
                    await cleanup.RestoreTransmitSettingsAsync(
                        previousTransmitSettings,
                        cancellationToken);
                    if (resources is not null)
                    {
                        await cleanup.RemoveOwnedTxResourcesAsync(
                            resources,
                            cancellationToken);
                    }
                }
                catch
                {
                }
            }
            throw;
        }
    }

    private async Task RestoreOnlyAsync(
        HilOptions options,
        HilTransmitSettings previousTransmitSettings,
        CancellationToken cancellationToken)
    {
        await using HilFlexSession cleanup =
            await ConnectCleanupGuiWithRetriesAsync(
                options,
                "idle transmit-setting restoration",
                cancellationToken);
        VerifyCleanupState(cleanup.Snapshot(), options.ExpectedSerial);
        // A newly connected cleanup GUI can briefly hold an earlier transmit
        // snapshot while the radio finishes its client transition. Always send
        // the complete target here instead of skipping commands from that first
        // snapshot; the subsequent fresh-session verification remains the
        // radio-authoritative acceptance check.
        await cleanup.ForceRestoreTransmitSettingsAsync(
            previousTransmitSettings,
            cancellationToken);
    }

    private (Process Child, Task<string> Errors) StartChild(
        string childPlanFile,
        string childToken)
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
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        bool dotnetHost = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
        start.FileName = processPath;
        if (dotnetHost)
        {
            start.ArgumentList.Add(entryAssembly);
        }
        start.ArgumentList.Add("internal-engine-process-child");
        start.ArgumentList.Add("--child-plan");
        start.ArgumentList.Add(childPlanFile);
        start.ArgumentList.Add("--child-token");
        start.ArgumentList.Add(childToken);

        Process child = Process.Start(start) ??
            throw new InvalidOperationException(
                "The HIL engine child process could not be started.");
        Task<string> errors = child.StandardError.ReadToEndAsync();
        return (child, errors);
    }

    private async Task<T> ReadChildMessageAsync<T>(
        Process child,
        Task<string> childErrors,
        string expectedType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where T : HilEngineChildMessage
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            string? line;
            try
            {
                line = await child.StandardOutput.ReadLineAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out waiting for child message '{expectedType}'.");
            }
            if (line is null)
            {
                if (!child.HasExited)
                {
                    await child.WaitForExitAsync(cancellationToken);
                }
                string errors = await childErrors;
                throw new InvalidOperationException(
                    $"The child process exited before '{expectedType}'. " +
                    $"Exit={child.ExitCode}; stderr={errors}");
            }
            if (!line.StartsWith(
                    HilEngineProcessChild.MessagePrefix,
                    StringComparison.Ordinal))
            {
                continue;
            }
            T message = JsonSerializer.Deserialize<T>(
                    line[HilEngineProcessChild.MessagePrefix.Length..],
                    JsonOptions) ??
                throw new InvalidOperationException(
                    "The child emitted an empty JSON message.");
            if (!string.Equals(
                    message.Type,
                    expectedType,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected child message '{expectedType}', received '{message.Type}'.");
            }
            return message;
        }
    }

    private async Task WaitForObserverOwnershipAsync(
        HilFlexSession observer,
        uint childHandle,
        CancellationToken cancellationToken)
    {
        await observer.WaitForAsync(
            snapshot =>
                snapshot.TxOccupancy.State == RadioTxOccupancyState.Idle &&
                snapshot.TxOccupancy.FreshUntil > m_timeProvider.GetUtcNow() &&
                snapshot.TxOccupancy.LocalPttOwners.Count == 1 &&
                snapshot.TxOccupancy.LocalPttOwners[0].ClientHandle ==
                    childHandle &&
                snapshot.GuiClients.Any(client =>
                    client.ClientHandle == childHandle &&
                    client.LocalPtt),
            TimeSpan.FromSeconds(5),
            cancellationToken);
    }

    private async Task<bool> WaitForFrequencyResourcesGoneAsync(
        HilFlexSession observer,
        long frequencyHz,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string expectedMhz =
            (frequencyHz / 1_000_000d).ToString(
                "0.000000",
                System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await observer.WaitForAsync(
                snapshot => snapshot.Slices.Values.All(fields =>
                    !fields.TryGetValue("RF_frequency", out string? value) ||
                    !string.Equals(
                        value,
                        expectedMhz,
                        StringComparison.Ordinal)),
                timeout,
                cancellationToken);
            return true;
        }
        catch (TimeoutException)
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
                "hil-engine-process-loss-watchdog",
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

    private async Task EmergencySupervisorUnkeyAsync(
        StationTxSafetySupervisor supervisor,
        CancellationToken cancellationToken)
    {
        try
        {
            StationTxSafetyResult abort = await supervisor.AbortAsync(
                "hil-process-loss-cleanup",
                cancellationToken);
            if (abort.Snapshot.State != StationTxSafetyState.Disarmed)
            {
                await WaitForSafetyAsync(
                    supervisor,
                    result => result.Snapshot.State == StationTxSafetyState.Disarmed,
                    "process-loss cleanup idle",
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            m_loggerFactory
                .CreateLogger<HilSafetyEngineProcessLossOperation>()
                .LogCritical(
                    exception,
                    "The independent observer could not confirm process-loss cleanup unkey");
        }
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
                "The process-loss test requires every external GUI client to be disconnected.");
        }
        if (snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= m_timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException(
                "The process-loss test requires a fresh idle interlock.");
        }
        if (snapshot.TransmitSettings is null)
        {
            throw new InvalidOperationException(
                "The radio did not provide a complete restorable transmit-route snapshot.");
        }
        HilTransmitSettings expectedBaseline = new(
            RfPower: 100,
            DaxEnabled: true,
            MicSelection: "PC",
            VoxEnabled: false);
        if (snapshot.TransmitSettings != expectedBaseline)
        {
            throw new InvalidOperationException(
                $"The process-loss test requires the known PSOC2 idle baseline {expectedBaseline}; observed {snapshot.TransmitSettings}. Run restore-idle-defaults before continuing.");
        }
        if (!snapshot.Cwx.HasFreshConfiguration(m_timeProvider.GetUtcNow()))
        {
            throw new InvalidOperationException(
                "The radio did not provide fresh restorable CWX configuration.");
        }
    }

    private void VerifyObserverState(
        HilRadioSnapshot snapshot,
        string expectedSerial)
    {
        if (!string.Equals(snapshot.Serial, expectedSerial, StringComparison.Ordinal) ||
            snapshot.GuiClients.Count != 0 ||
            snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= m_timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException(
                "The process-loss observer did not start on exact PSOC2 idle state with an empty GUI roster.");
        }
    }

    private void VerifyCleanupState(
        HilRadioSnapshot snapshot,
        string expectedSerial)
    {
        if (!string.Equals(snapshot.Serial, expectedSerial, StringComparison.Ordinal) ||
            snapshot.ExternalGuiClients.Count != 0 ||
            snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= m_timeProvider.GetUtcNow())
        {
            throw new InvalidOperationException(
                "The post-process-loss cleanup session did not receive exact PSOC2 idle ownership.");
        }
    }

    private static void VerifyReady(
        HilEngineChildReady ready,
        HilEngineProcessChildPlan plan,
        Process child)
    {
        if (ready.ProcessId != child.Id ||
            ready.ClientHandle == 0 ||
            !string.Equals(
                ready.EngineInstanceId,
                plan.EngineInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(ready.SessionId, plan.SessionId, StringComparison.Ordinal) ||
            !string.Equals(
                ready.BrowserClientId,
                plan.BrowserClientId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(ready.LeaseId) ||
            ready.EngineCommands.Key != 0 ||
            ready.EngineCommands.Unkey != 0)
        {
            throw new InvalidOperationException(
                "The child ready evidence did not match the one-time process plan.");
        }
    }

    private static StationTxEngineConnectionObservation Observation(
        HilEngineChildReady ready,
        bool isConnected) =>
        new(
            ready.EngineInstanceId,
            ready.LeaseId,
            ready.ClientHandle,
            isConnected);

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

    private sealed class CountingEmergencyUnkeyTransport(
        IStationTxEmergencyUnkeyTransport inner,
        TimeProvider timeProvider)
        : IStationTxEmergencyUnkeyTransport
    {
        public bool IsConnected => inner.IsConnected;
        public int CommandCount { get; private set; }
        public DateTimeOffset? CommandDispatchedAt { get; private set; }
        public DateTimeOffset? CommandCompletedAt { get; private set; }

        public async Task<StationTxTransportResult> RequestUnkeyAsync(
            CancellationToken cancellationToken)
        {
            CommandCount++;
            CommandDispatchedAt ??= timeProvider.GetUtcNow();
            try
            {
                return await inner.RequestUnkeyAsync(cancellationToken);
            }
            finally
            {
                CommandCompletedAt = timeProvider.GetUtcNow();
            }
        }
    }

    private abstract record HilEngineChildMessage(string Type);

    private sealed record HilEngineChildCommandCounts(int Key, int Unkey);

    private sealed record HilEngineChildResources(
        string PanId,
        string WaterfallId,
        int SliceId);

    private sealed record HilEngineChildReady(
        string Type,
        int ProcessId,
        string EngineInstanceId,
        string SessionId,
        string BrowserClientId,
        string LeaseId,
        DateTimeOffset LeaseExpiresAt,
        uint ClientHandle,
        HilEngineChildResources Resources,
        HilEngineChildCommandCounts EngineCommands)
        : HilEngineChildMessage(Type);

    private sealed record HilEngineChildKeyed(
        string Type,
        int ProcessId,
        uint ClientHandle,
        DateTimeOffset KeyedAt,
        string GateState,
        HilEngineChildCommandCounts EngineCommands)
        : HilEngineChildMessage(Type);

    private sealed record HilEngineChildIdleReconciled(
        string Type,
        int ProcessId,
        uint ClientHandle,
        DateTimeOffset ReconciledAt,
        string GateState,
        bool ActiveIntent,
        string TxState,
        HilEngineChildCommandCounts EngineCommands)
        : HilEngineChildMessage(Type);

    private sealed record HilEngineRestartReconciliation(
        int ProcessId,
        string EngineInstanceId,
        string SessionId,
        string BrowserClientId,
        string LeaseId,
        uint ClientHandle,
        int ExitCode,
        bool OldHandleAbsent,
        bool AllIdentitiesFresh,
        bool ResourcesGone,
        bool BaselineRestored,
        DateTimeOffset StartedAt,
        DateTimeOffset ReadyAt,
        DateTimeOffset ReconciledAt,
        DateTimeOffset ExitedAt,
        HilEngineChildCommandCounts EngineCommands);
}
