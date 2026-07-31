using System.Text;
using AetherSDR.TxWatchdog.Protocol;

namespace AetherSDR.TxWatchdog;

public static class WatchdogStdioServer
{
    public static async Task RunAsync(
        TextReader input,
        TextWriter output,
        WatchdogHostEngine engine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(engine);

        while (true)
        {
            BoundedLineResult line = await ReadBoundedLineAsync(
                input,
                WatchdogProtocol.MaximumMessageCharacters,
                cancellationToken);
            if (line.EndOfStream)
            {
                return;
            }

            WatchdogResponse response;
            if (line.TooLarge)
            {
                response = engine.Reject("invalid", "message-too-large");
            }
            else if (!WatchdogProtocol.TryParseRequest(
                         line.Value ?? string.Empty,
                         out WatchdogRequest? request,
                         out string error) ||
                     request is null)
            {
                response = engine.Reject("invalid", error);
            }
            else
            {
                response = engine.Process(request);
            }

            await output.WriteLineAsync(
                WatchdogProtocol.SerializeResponse(response));
            await output.FlushAsync(cancellationToken);
        }
    }

    internal static async Task<BoundedLineResult> ReadBoundedLineAsync(
        TextReader input,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);

        StringBuilder builder = new(Math.Min(maximumCharacters, 256));
        char[] oneCharacter = new char[1];
        bool tooLarge = false;
        bool sawAny = false;

        while (true)
        {
            int count = await input.ReadAsync(
                oneCharacter.AsMemory(0, 1),
                cancellationToken);
            if (count == 0)
            {
                if (!sawAny)
                {
                    return new BoundedLineResult(
                        EndOfStream: true,
                        TooLarge: false,
                        Value: null);
                }
                return new BoundedLineResult(
                    EndOfStream: false,
                    tooLarge,
                    tooLarge ? null : TrimCarriageReturn(builder.ToString()));
            }

            sawAny = true;
            char value = oneCharacter[0];
            if (value == '\n')
            {
                return new BoundedLineResult(
                    EndOfStream: false,
                    tooLarge,
                    tooLarge ? null : TrimCarriageReturn(builder.ToString()));
            }

            if (tooLarge)
            {
                continue;
            }
            if (builder.Length == maximumCharacters)
            {
                tooLarge = true;
                builder.Clear();
                continue;
            }
            builder.Append(value);
        }
    }

    private static string TrimCarriageReturn(string value) =>
        value.EndsWith('\r') ? value[..^1] : value;

    internal sealed record BoundedLineResult(
        bool EndOfStream,
        bool TooLarge,
        string? Value);
}
