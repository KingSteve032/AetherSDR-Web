using System.Collections.Concurrent;
using System.Diagnostics;
using AetherSDR.TxWatchdog.Protocol;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class StationTxIndependentWatchdogClientTests
{
    [Fact]
    public async Task SupervisedProcessStartsDisarmedAndResetsAfterDisconnect()
    {
        ConcurrentQueue<StationTxIndependentWatchdogEvent> events = new();
        await using StationTxIndependentWatchdogClient client = CreateClient(
            watchdogEvent =>
            {
                events.Enqueue(watchdogEvent);
                return ValueTask.CompletedTask;
            });

        await client.StartAsync();
        StationTxIndependentWatchdogDiagnostics started = client.Snapshot;
        Assert.True(started.ProcessRunning);
        Assert.True(started.IpcConnected);
        Assert.Equal("Disarmed", started.State);
        Assert.False(started.Registered);
        Assert.False(started.Connected);
        Assert.False(started.LeaseBound);
        Assert.False(started.RadioCommandTransportAvailable);
        Assert.False(started.ArmingAvailable);
        string firstHost = Assert.IsType<string>(started.HostInstanceId);

        WatchdogIdentity identity = Identity("lease-a");
        await client.RegisterAsync(identity);
        StationTxIndependentWatchdogDiagnostics registered = client.Snapshot;
        Assert.True(registered.Registered);
        Assert.True(registered.Connected);
        Assert.True(registered.LeaseBound);
        Assert.Equal(1, registered.LastSequence);

        await client.HeartbeatAsync(identity);
        Assert.Equal(2, client.Snapshot.LastSequence);

        await client.DisconnectAndResetAsync(identity);
        StationTxIndependentWatchdogDiagnostics reset = client.Snapshot;
        Assert.True(reset.ProcessRunning);
        Assert.True(reset.IpcConnected);
        Assert.NotEqual(firstHost, reset.HostInstanceId);
        Assert.False(reset.Registered);
        Assert.False(reset.Connected);
        Assert.False(reset.LeaseBound);
        Assert.Equal(0, reset.LastSequence);
        Assert.DoesNotContain(
            events,
            watchdogEvent => watchdogEvent.Kind ==
                StationTxIndependentWatchdogEventKind.Lost);
    }

    [Fact]
    public async Task ForcedChildExitPublishesLossAndRestartsEmpty()
    {
        TaskCompletionSource<StationTxIndependentWatchdogEvent> lost = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using StationTxIndependentWatchdogClient client = CreateClient(
            watchdogEvent =>
            {
                if (watchdogEvent.Kind ==
                    StationTxIndependentWatchdogEventKind.Lost)
                {
                    lost.TrySetResult(watchdogEvent);
                }
                return ValueTask.CompletedTask;
            });

        await client.StartAsync();
        await client.RegisterAsync(Identity("lease-a"));
        StationTxIndependentWatchdogDiagnostics registered = client.Snapshot;
        int processId = Assert.IsType<int>(registered.ProcessId);
        string firstHost = Assert.IsType<string>(registered.HostInstanceId);

        using (Process process = Process.GetProcessById(processId))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        StationTxIndependentWatchdogEvent loss = await lost.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.Equal("watchdog-process-exited", loss.Reason);

        StationTxIndependentWatchdogDiagnostics restarted =
            await WaitForAsync(
                client,
                snapshot => snapshot.ProcessRunning &&
                    snapshot.IpcConnected &&
                    snapshot.RestartCount >= 1 &&
                    !string.Equals(
                        snapshot.HostInstanceId,
                        firstHost,
                        StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
        Assert.Equal("Disarmed", restarted.State);
        Assert.False(restarted.Registered);
        Assert.False(restarted.Connected);
        Assert.False(restarted.LeaseBound);
        Assert.Equal(0, restarted.LastSequence);
        Assert.False(restarted.RadioCommandTransportAvailable);
        Assert.False(restarted.ArmingAvailable);
    }

    [Fact]
    public async Task ProcessLossIsPublishedBeforeTheRestartDelay()
    {
        TaskCompletionSource<StationTxIndependentWatchdogEvent> lost = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using StationTxIndependentWatchdogClient client = CreateClient(
            watchdogEvent =>
            {
                if (watchdogEvent.Kind ==
                    StationTxIndependentWatchdogEventKind.Lost)
                {
                    lost.TrySetResult(watchdogEvent);
                }
                return ValueTask.CompletedTask;
            },
            restartDelayMilliseconds: 1000);

        await client.StartAsync();
        await client.RegisterAsync(Identity("lease-a"));
        int processId = Assert.IsType<int>(client.Snapshot.ProcessId);
        Stopwatch elapsed = Stopwatch.StartNew();
        using (Process process = Process.GetProcessById(processId))
        {
            process.Kill(entireProcessTree: true);
        }

        StationTxIndependentWatchdogEvent loss = await lost.Task.WaitAsync(
            TimeSpan.FromMilliseconds(750));
        elapsed.Stop();
        Assert.Equal("watchdog-process-exited", loss.Reason);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromMilliseconds(750),
            $"Loss notification took {elapsed.Elapsed}.");
        Assert.False(client.Snapshot.Registered);
        Assert.False(client.Snapshot.LeaseBound);
    }

    [Fact]
    public async Task MissingBinaryFailsClosedWithoutBlockingReceiveStartup()
    {
        TaskCompletionSource<StationTxIndependentWatchdogEvent> lost = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using StationTxIndependentWatchdogClient client = new(
            new StationTxIndependentWatchdogOwner(
                "RADIO-A",
                "session-a",
                "browser-a",
                "gateway-a",
                "engine-a"),
            new IndependentTxWatchdogSettings
            {
                Enabled = true,
                RequestTimeoutMilliseconds = 500,
                RestartDelayMilliseconds = 30000
            },
            new IndependentTxWatchdogLaunchCommand(
                Path.Combine(
                    Path.GetTempPath(),
                    Guid.NewGuid().ToString("N"),
                    "missing-watchdog"),
                ["--stdio"]),
            watchdogEvent =>
            {
                if (watchdogEvent.Kind ==
                    StationTxIndependentWatchdogEventKind.Lost)
                {
                    lost.TrySetResult(watchdogEvent);
                }
                return ValueTask.CompletedTask;
            },
            disposedCallback: () => { },
            NullLogger<StationTxIndependentWatchdogClient>.Instance);

        await client.StartAsync();
        StationTxIndependentWatchdogEvent loss = await lost.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Equal("watchdog-binary-missing", loss.Reason);
        Assert.False(client.Snapshot.ProcessRunning);
        Assert.False(client.Snapshot.IpcConnected);
        Assert.False(client.Snapshot.Registered);
        Assert.False(client.Snapshot.RadioCommandTransportAvailable);
        Assert.False(client.Snapshot.ArmingAvailable);
    }

    [Fact]
    public async Task DifferentIdentityFailsClosedAndRestartsEmpty()
    {
        TaskCompletionSource<StationTxIndependentWatchdogEvent> lost = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using StationTxIndependentWatchdogClient client = CreateClient(
            watchdogEvent =>
            {
                if (watchdogEvent.Kind ==
                    StationTxIndependentWatchdogEventKind.Lost)
                {
                    lost.TrySetResult(watchdogEvent);
                }
                return ValueTask.CompletedTask;
            });

        await client.StartAsync();
        await client.RegisterAsync(Identity("lease-a"));
        await client.HeartbeatAsync(Identity("lease-b"));

        StationTxIndependentWatchdogEvent loss = await lost.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.Equal("local-identity-mismatch", loss.Reason);
        StationTxIndependentWatchdogDiagnostics snapshot = await WaitForAsync(
            client,
            current => current.ProcessRunning &&
                current.IpcConnected &&
                current.RestartCount >= 1,
            TimeSpan.FromSeconds(5));
        Assert.False(snapshot.Registered);
        Assert.False(snapshot.LeaseBound);
        Assert.Equal(0, snapshot.LastSequence);
    }

    private static StationTxIndependentWatchdogClient CreateClient(
        Func<StationTxIndependentWatchdogEvent, ValueTask> eventSink,
        int restartDelayMilliseconds = 100)
    {
        string hostAssembly = typeof(global::AetherSDR.TxWatchdog.Program)
            .Assembly.Location;
        string dotnetHost =
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            Environment.ProcessPath ??
            throw new InvalidOperationException("The dotnet host path is unavailable.");
        Assert.True(File.Exists(dotnetHost), dotnetHost);
        Assert.True(File.Exists(hostAssembly), hostAssembly);
        return new StationTxIndependentWatchdogClient(
            new StationTxIndependentWatchdogOwner(
                "RADIO-A",
                "session-a",
                "browser-a",
                "gateway-a",
                "engine-a"),
            new IndependentTxWatchdogSettings
            {
                Enabled = true,
                RequestTimeoutMilliseconds = 2000,
                RestartDelayMilliseconds = restartDelayMilliseconds
            },
            new IndependentTxWatchdogLaunchCommand(
                dotnetHost,
                [hostAssembly, "--stdio"]),
            eventSink,
            disposedCallback: () => { },
            NullLogger<StationTxIndependentWatchdogClient>.Instance);
    }

    private static WatchdogIdentity Identity(string leaseId) =>
        new(
            "RADIO-A",
            "session-a",
            "browser-a",
            "gateway-a",
            "engine-a",
            "connection-a",
            leaseId,
            0x12345678);

    private static async Task<StationTxIndependentWatchdogDiagnostics>
        WaitForAsync(
            StationTxIndependentWatchdogClient client,
            Func<StationTxIndependentWatchdogDiagnostics, bool> predicate,
            TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);
        while (!cancellation.IsCancellationRequested)
        {
            StationTxIndependentWatchdogDiagnostics snapshot = client.Snapshot;
            if (predicate(snapshot))
            {
                return snapshot;
            }
            await Task.Delay(25, cancellation.Token);
        }
        throw new TimeoutException(
            "The independent watchdog did not reach the expected state.");
    }
}
