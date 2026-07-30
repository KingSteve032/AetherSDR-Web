namespace AetherSDR.Web.Radio;

public interface IRadioIntentTransport
{
    Task<IntentResult> ApplyAsync(
        ControlIntent intent,
        long currentVersion,
        CancellationToken cancellationToken);
}
