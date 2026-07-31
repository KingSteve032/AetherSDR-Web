using System.Diagnostics;
using System.Text.Json;
using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog.Tests;

public sealed class WatchdogProcessBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task ForcedProcessLossCannotRestoreObservedIdentityOnRestart()
    {
        using Process first = StartHost();
        WatchdogResponse firstStatus = await SendAsync(
            first,
            WatchdogProtocolTests.StatusJson("status-first"));
        WatchdogResponse registered = await SendAsync(
            first,
            WatchdogProtocolTests.RequestJson(
                "register",
                "register-first",
                1,
                WatchdogProtocolTests.IdentityJson("radio-a")));

        Assert.True(firstStatus.Ok);
        Assert.True(registered.Ok);
        Assert.True(registered.Snapshot.Registered);
        Assert.True(registered.Snapshot.Connected);
        string firstHostInstanceId = registered.Snapshot.HostInstanceId;

        first.Kill(entireProcessTree: true);
        await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        using Process second = StartHost();
        try
        {
            WatchdogResponse restarted = await SendAsync(
                second,
                WatchdogProtocolTests.StatusJson("status-second"));

            Assert.True(restarted.Ok);
            Assert.NotEqual(
                firstHostInstanceId,
                restarted.Snapshot.HostInstanceId);
            Assert.Equal("Disarmed", restarted.Snapshot.State);
            Assert.Equal(
                "command-incapable-skeleton",
                restarted.Snapshot.Reason);
            Assert.False(restarted.Snapshot.RadioCommandTransportAvailable);
            Assert.False(restarted.Snapshot.ArmingAvailable);
            Assert.False(restarted.Snapshot.Registered);
            Assert.False(restarted.Snapshot.Connected);
            Assert.Null(restarted.Snapshot.Identity);
            Assert.False(restarted.Snapshot.LeaseBound);
            Assert.Equal(0, restarted.Snapshot.LastSequence);
            Assert.Equal(
                "process-started-disarmed",
                restarted.Snapshot.LastObservation);
        }
        finally
        {
            await StopAsync(second);
        }
    }

    [Fact]
    public async Task UnknownMutationRequestIsRejectedByTheSeparateProcess()
    {
        using Process process = StartHost();
        try
        {
            WatchdogResponse response = await SendAsync(
                process,
                WatchdogProtocolTests.RequestJson(
                    "arm",
                    "mutation-1",
                    1,
                    WatchdogProtocolTests.IdentityJson("radio-a")));

            Assert.False(response.Ok);
            Assert.Equal("unknown-request-type", response.Error);
            Assert.Equal("Disarmed", response.Snapshot.State);
            Assert.False(response.Snapshot.Registered);
            Assert.False(response.Snapshot.LeaseBound);
            Assert.False(response.Snapshot.RadioCommandTransportAvailable);
            Assert.False(response.Snapshot.ArmingAvailable);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    private static Process StartHost()
    {
        string hostAssembly = typeof(global::AetherSDR.TxWatchdog.Program)
            .Assembly.Location;
        Assert.True(File.Exists(hostAssembly), hostAssembly);
        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("--stdio");
        Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the watchdog host.");
        return process;
    }

    private static async Task<WatchdogResponse> SendAsync(
        Process process,
        string request)
    {
        await process.StandardInput.WriteLineAsync(request);
        await process.StandardInput.FlushAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        string? responseLine = await process.StandardOutput.ReadLineAsync(
            timeout.Token);
        if (responseLine is null)
        {
            string error = await process.StandardError.ReadToEndAsync(
                timeout.Token);
            throw new InvalidOperationException(
                $"The watchdog host returned no response. {error}");
        }
        return JsonSerializer.Deserialize<WatchdogResponse>(
                   responseLine,
                   JsonOptions) ??
            throw new InvalidOperationException(
                "A watchdog response was required.");
    }

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
