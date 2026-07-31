namespace AetherSDR.TxWatchdog;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 ||
            !string.Equals(args[0], "--stdio", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync(
                "Usage: AetherSDR.TxWatchdog --stdio");
            return 2;
        }

        WatchdogHostEngine engine = new();
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
