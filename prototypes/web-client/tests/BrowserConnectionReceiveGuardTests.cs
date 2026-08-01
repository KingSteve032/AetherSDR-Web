using System.Net.WebSockets;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class BrowserConnectionReceiveGuardTests
{
    [Fact]
    public async Task CompletedReceiveReturnsTheExactResult()
    {
        WebSocketReceiveResult expected = new(
            17,
            WebSocketMessageType.Text,
            endOfMessage: true);

        BrowserReceiveAttempt attempt =
            await BrowserConnectionReceiveGuard.ReceiveAsync(
                _ => Task.FromResult(expected),
                CancellationToken.None,
                TimeSpan.FromSeconds(1));

        Assert.False(attempt.TimedOut);
        Assert.Same(expected, attempt.Result);
    }

    [Fact]
    public async Task SilentBrowserReceiveTimesOutFailClosed()
    {
        BrowserReceiveAttempt attempt =
            await BrowserConnectionReceiveGuard.ReceiveAsync(
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new WebSocketReceiveResult(
                        0,
                        WebSocketMessageType.Close,
                        endOfMessage: true);
                },
                CancellationToken.None,
                TimeSpan.FromMilliseconds(25));

        Assert.True(attempt.TimedOut);
        Assert.Null(attempt.Result);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAHeartbeatTimeout()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BrowserConnectionReceiveGuard.ReceiveAsync(
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new WebSocketReceiveResult(
                        0,
                        WebSocketMessageType.Close,
                        endOfMessage: true);
                },
                cancellation.Token,
                TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void DefaultTimeoutReleasesTheSocketBeforeSessionCleanup()
    {
        Assert.True(BrowserConnectionReceiveGuard.DefaultTimeout >
                    TimeSpan.FromSeconds(10));
        Assert.True(BrowserConnectionReceiveGuard.DefaultTimeout <
                    RadioSessionRegistry.IdleTimeout);
    }
}
