using System.Text.Json;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging;

namespace AetherSDR.TxHil;

internal sealed class HilEngineProcessChild(
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null)
{
    public const string MessagePrefix = "AETHER_ENGINE_CHILD:";
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);
    private const int ConnectAttempts = 3;

    private readonly ILoggerFactory m_loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly TimeProvider m_timeProvider =
        timeProvider ?? TimeProvider.System;

    public async Task<int> RunAsync(
        HilEngineProcessChildOptions childOptions,
        CancellationToken cancellationToken)
    {
        HilEngineProcessChildPlan plan =
            await HilEngineProcessChildPlan.ConsumeAsync(
                childOptions.PlanFile,
                childOptions.Token,
                m_timeProvider,
                cancellationToken);
        plan.VerifyParentProcess();
        HilOptions options = plan.ToHilOptions();

        await using HilFlexSession engine =
            await ConnectWithRetriesAsync(options, cancellationToken);

        HilRadioSnapshot initial = engine.Snapshot();
        VerifyInitialState(initial, options.ExpectedSerial);
        HilTransmitSettings previousSettings = initial.TransmitSettings!;
        HilOwnedRadioResources? resources = null;
        TxLeaseManager? leases = null;
        TxLease? lease = null;
        StationTxCommandGate? gate = null;
        CountingTxCommandTransport? transport = null;

        try
        {
            resources = await engine.CreateOwnedTxResourcesAsync(
                options,
                cancellationToken);
            HilTransmitSettings captured =
                await engine.ConfigureSilentTransmitAsync(cancellationToken);
            if (captured != previousSettings)
            {
                throw new InvalidOperationException(
                    "The transmit settings changed after the child safety snapshot.");
            }
            await engine.RequestLocalPttAsync(cancellationToken);

            leases = new TxLeaseManager(m_timeProvider);
            if (!leases.TryAcquire(
                    options.RadioId,
                    plan.SessionId,
                    plan.BrowserClientId,
                    "tx-hil",
                    "PSOC2 Engine Process Loss",
                    LeaseLifetime,
                    out lease,
                    out string? leaseError))
            {
                throw new InvalidOperationException(
                    leaseError ?? "The child could not acquire its TX lease.");
            }

            transport = new CountingTxCommandTransport(resources.Transport);
            gate = new StationTxCommandGate(
                allowTransmit: true,
                options.RadioId,
                leases,
                engine.OccupancyRegistry,
                transport,
                m_timeProvider);

            await WriteMessageAsync(new
            {
                type = "ready",
                processId = Environment.ProcessId,
                plan.EngineInstanceId,
                plan.SessionId,
                plan.BrowserClientId,
                leaseId = lease!.LeaseId,
                leaseExpiresAt = lease.ExpiresAt,
                clientHandle = engine.ClientHandle,
                resources = new
                {
                    resources.PanId,
                    resources.WaterfallId,
                    resources.SliceId
                },
                engineCommands = new
                {
                    key = transport.KeyCommands,
                    unkey = transport.UnkeyCommands
                }
            });

            string? instruction = await Console.In.ReadLineAsync(
                cancellationToken);
            if (string.Equals(
                    instruction,
                    "reconcile-idle-and-exit",
                    StringComparison.Ordinal))
            {
                HilRadioSnapshot reconciled = engine.Snapshot();
                if (gate.Snapshot.State != StationTxGateState.Idle ||
                    gate.Snapshot.HasActiveIntent ||
                    transport.KeyCommands != 0 ||
                    transport.UnkeyCommands != 0 ||
                    reconciled.TxOccupancy.State != RadioTxOccupancyState.Idle ||
                    reconciled.TxOccupancy.FreshUntil <=
                        m_timeProvider.GetUtcNow() ||
                    !reconciled.TxOccupancy.HasExclusiveLocalPttAuthority(
                        engine.ClientHandle))
                {
                    throw new InvalidOperationException(
                        "The replacement engine could not reconcile from exact fresh idle without TX intent.");
                }

                await WriteMessageAsync(new
                {
                    type = "idle-reconciled",
                    processId = Environment.ProcessId,
                    clientHandle = engine.ClientHandle,
                    reconciledAt = m_timeProvider.GetUtcNow(),
                    gateState = gate.Snapshot.State.ToString(),
                    activeIntent = gate.Snapshot.HasActiveIntent,
                    txState = reconciled.TxOccupancy.StateName,
                    engineCommands = new
                    {
                        key = transport.KeyCommands,
                        unkey = transport.UnkeyCommands
                    }
                });
                return 0;
            }
            if (!string.Equals(instruction, "key", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The child did not receive an exact supported one-time instruction.");
            }

            StationTxGateResult key = await gate.RequestKeyAsync(
                lease.LeaseId,
                plan.SessionId,
                plan.BrowserClientId,
                cancellationToken);
            if (!key.Success &&
                key.Snapshot.State != StationTxGateState.KeyPending)
            {
                throw new InvalidOperationException(
                    $"The child key request failed closed: {key.Code}: {key.Message}");
            }
            StationTxGateResult keyed = await WaitForGateStateAsync(
                gate,
                StationTxGateState.Keyed,
                TimeSpan.FromSeconds(3),
                cancellationToken);

            await WriteMessageAsync(new
            {
                type = "keyed",
                processId = Environment.ProcessId,
                clientHandle = engine.ClientHandle,
                keyedAt = m_timeProvider.GetUtcNow(),
                gateState = keyed.Snapshot.State.ToString(),
                engineCommands = new
                {
                    key = transport.KeyCommands,
                    unkey = transport.UnkeyCommands
                }
            });

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        finally
        {
            using CancellationTokenSource cleanup =
                new(TimeSpan.FromSeconds(15));
            if (gate is not null)
            {
                try
                {
                    if (lease is not null && gate.Snapshot.HasActiveIntent)
                    {
                        await gate.RequestUnkeyAsync(
                            lease.LeaseId,
                            plan.SessionId,
                            plan.BrowserClientId,
                            cleanup.Token);
                        await WaitForGateStateAsync(
                            gate,
                            StationTxGateState.Idle,
                            TimeSpan.FromSeconds(5),
                            cleanup.Token);
                    }
                }
                catch (Exception exception)
                {
                    m_loggerFactory.CreateLogger<HilEngineProcessChild>()
                        .LogCritical(
                            exception,
                            "The child remained alive but could not confirm cleanup unkey");
                }
                await gate.DisposeAsync();
            }
            if (leases is not null && lease is not null)
            {
                leases.TryRelease(
                    options.RadioId,
                    lease.LeaseId,
                    plan.SessionId,
                    plan.BrowserClientId,
                    "child-cleanup",
                    out _);
            }

            try
            {
                HilRadioSnapshot snapshot = engine.Snapshot();
                if (snapshot.TxOccupancy.State == RadioTxOccupancyState.Idle)
                {
                    await engine.RestoreTransmitSettingsAsync(
                        previousSettings,
                        cleanup.Token);
                    if (resources is not null)
                    {
                        await engine.RemoveOwnedTxResourcesAsync(
                            resources,
                            cleanup.Token);
                    }
                }
            }
            catch (Exception exception)
            {
                m_loggerFactory.CreateLogger<HilEngineProcessChild>()
                    .LogCritical(
                        exception,
                        "The child remained alive but could not restore its radio resources");
            }
        }
    }

    private async Task<HilFlexSession> ConnectWithRetriesAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= ConnectAttempts; attempt++)
        {
            HilFlexSession session = new(
                options.RadioId,
                m_loggerFactory.CreateLogger<HilFlexSession>());
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
                if (attempt == ConnectAttempts)
                {
                    break;
                }
                m_loggerFactory.CreateLogger<HilEngineProcessChild>()
                    .LogWarning(
                        exception,
                        "Transient FLEX connection failure while starting the engine child; retrying with a fresh session ({Attempt}/{MaximumAttempts})",
                        attempt,
                        ConnectAttempts);
                await Task.Delay(
                    ConnectRetryDelay,
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
            $"The engine child could not establish a fresh FLEX GUI session after {ConnectAttempts} attempts.",
            lastException);
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
                "engine-process-child",
                cancellationToken);
            if (last.Snapshot.State == expected)
            {
                return last;
            }
            if (last.Snapshot.State == StationTxGateState.Faulted)
            {
                throw new InvalidOperationException(
                    $"The child TX gate faulted: {last.Code}: {last.Message}");
            }
            await Task.Delay(PollInterval, m_timeProvider, cancellationToken);
        }
        throw new TimeoutException(
            $"The child TX gate did not reach {expected}; last state was " +
            $"{last.Snapshot.State} ({last.Code}: {last.Message}).");
    }

    private static void VerifyInitialState(
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
                "The engine child requires every external GUI client to be disconnected.");
        }
        if (snapshot.TxOccupancy.State != RadioTxOccupancyState.Idle ||
            snapshot.TxOccupancy.FreshUntil <= TimeProvider.System.GetUtcNow())
        {
            throw new InvalidOperationException(
                "The engine child requires a fresh idle interlock.");
        }
        HilTransmitSettings expectedBaseline =
            new(100, true, "PC", false);
        if (snapshot.TransmitSettings != expectedBaseline)
        {
            throw new InvalidOperationException(
                "The engine child requires the exact 100 W, DAX-on, PC-mic, VOX-off station baseline.");
        }
    }

    private static async Task WriteMessageAsync(object message)
    {
        await Console.Out.WriteLineAsync(
            MessagePrefix + JsonSerializer.Serialize(message));
        await Console.Out.FlushAsync();
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
            return await inner.SetTransmitAsync(enabled, cancellationToken);
        }
    }
}
