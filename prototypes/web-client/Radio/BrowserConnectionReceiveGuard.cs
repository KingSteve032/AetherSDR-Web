using System.Net.WebSockets;

namespace AetherSDR.Web.Radio;

internal sealed record BrowserReceiveAttempt(
    WebSocketReceiveResult? Result,
    bool TimedOut);

internal static class BrowserConnectionReceiveGuard
{
    internal static readonly TimeSpan DefaultTimeout =
        TimeSpan.FromSeconds(30);

    internal static async Task<BrowserReceiveAttempt> ReceiveAsync(
        Func<CancellationToken, Task<WebSocketReceiveResult>> receive,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(receive);

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero ||
            effectiveTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The browser receive timeout must be positive and no longer than two minutes.");
        }

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(effectiveTimeout);

        try
        {
            WebSocketReceiveResult result =
                await receive(timeoutSource.Token).ConfigureAwait(false);
            return new BrowserReceiveAttempt(result, TimedOut: false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new BrowserReceiveAttempt(Result: null, TimedOut: true);
        }
    }
}
