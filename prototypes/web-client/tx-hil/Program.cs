using AetherSDR.TxHil;
using Microsoft.Extensions.Logging;

using CancellationTokenSource lifetime = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

try
{
    bool internalEngineChild =
        args.Length > 0 &&
        string.Equals(
            args[0],
            "internal-engine-process-child",
            StringComparison.Ordinal);
    bool internalGatewayChild =
        args.Length > 0 &&
        string.Equals(
            args[0],
            "internal-gateway-authority-child",
            StringComparison.Ordinal);
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
    });
    if (internalEngineChild)
    {
        HilEngineProcessChildOptions childOptions =
            HilEngineProcessChildOptions.Parse(args[1..]);
        HilEngineProcessChild child = new(loggerFactory);
        return await child.RunAsync(childOptions, lifetime.Token);
    }
    if (internalGatewayChild)
    {
        return await HilGatewayAuthorityChild.RunAsync(lifetime.Token);
    }

    HilOptions options = HilOptions.Parse(args);
    HilRunner runner = new(loggerFactory);
    return await runner.RunAsync(options, lifetime.Token);
}
catch (HilUsageException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("HIL operation cancelled.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine("HIL operation failed:");
    Console.Error.WriteLine(exception);
    return 1;
}
