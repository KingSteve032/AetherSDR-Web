namespace AetherSDR.TxWatchdog;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!WatchdogProgramOptions.TryParse(
                args,
                out WatchdogProgramOptions? options,
                out string error))
        {
            await Console.Error.WriteLineAsync(
                $"{WatchdogProgramOptions.Usage} ({error})");
            return 2;
        }

        IWatchdogUnkeyTransport unkeyTransport =
            options!.UnkeyTransport.Enabled
                ? new FlexWatchdogUnkeyTransport(options.UnkeyTransport)
                : new UnavailableWatchdogUnkeyTransport();
        await using WatchdogHostEngine engine = new(
            timeProvider: null,
            hostInstanceId: null,
            unkeyTransport,
            options.ArmingEnabled);
        try
        {
            await WatchdogStdioServer.RunAsync(
                Console.In,
                Console.Out,
                engine,
                CancellationToken.None);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (IOException exception)
        {
            await Console.Error.WriteLineAsync(
                $"Watchdog stdio transport failed: {exception.Message}");
            return 1;
        }
    }
}
