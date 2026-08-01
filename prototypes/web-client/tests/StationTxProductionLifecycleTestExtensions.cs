using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

internal static class StationTxProductionLifecycleTestExtensions
{
    public static Task<StationTxCommandSessionCompositionResult>
        SubmitValidatedBrowserTxIntentAsync(
            this StationTxProductionLifecycle lifecycle,
            string connectionClientId,
            long sequence,
            BrowserTxIntent intent,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
    {
        System.Reflection.FieldInfo field =
            typeof(StationTxProductionLifecycle).GetField(
                "m_stationCommandComposition",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance) ??
            throw new InvalidOperationException(
                "The private command-session composition was not found.");
        StationTxCommandSessionComposition composition =
            Assert.IsType<StationTxCommandSessionComposition>(
                field.GetValue(lifecycle));
        return composition.SubmitAsync(
            new StationTxCommandSessionCompositionRequest(
                connectionClientId,
                sequence,
                intent,
                observedAt),
            cancellationToken);
    }
}
