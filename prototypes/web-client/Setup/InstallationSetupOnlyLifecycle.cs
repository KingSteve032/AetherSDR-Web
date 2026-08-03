using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AetherSDR.Web.Setup;

public enum InstallationSetupOnlyLifecycleStopReason
{
    None = 0,
    SetupComplete = 1,
    GatewayNoLongerRunsHere = 2,
    StateUnavailable = 3,
    StateInvalid = 4,
    SetupIdentityChanged = 5,
    RevisionRegressed = 6
}

public sealed record InstallationSetupOnlyLifecycleDecision(
    InstallationSetupOnlyLifecycleStopReason StopReason,
    long? SetupRevision,
    InstallationSetupLockMode? LockMode,
    InstallationSetupStep? LastCompletedStep)
{
    public bool ShouldStop => StopReason != InstallationSetupOnlyLifecycleStopReason.None;
}

public sealed class InstallationSetupOnlyLifecycleEvaluator
{
    private readonly InstallationSetupStore m_store;
    private readonly InstallationSetupOnlyIdentity m_identity;
    private long m_lastObservedRevision;

    public InstallationSetupOnlyLifecycleEvaluator(
        InstallationSetupStore store,
        InstallationSetupOnlyIdentity identity)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (identity.SetupSchemaVersion != InstallationSetupState.CurrentSchemaVersion ||
            identity.SetupCreatedAt == default ||
            identity.InitialRevision < 0)
        {
            throw new InvalidOperationException(
                "Setup-only lifecycle monitoring requires one valid startup identity.");
        }
        m_lastObservedRevision = identity.InitialRevision;
    }

    public async Task<InstallationSetupOnlyLifecycleDecision> EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        InstallationSetupState state;
        try
        {
            state = await m_store.LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Stop(InstallationSetupOnlyLifecycleStopReason.StateUnavailable);
        }
        catch (InvalidOperationException)
        {
            return Stop(InstallationSetupOnlyLifecycleStopReason.StateInvalid);
        }

        if (state.SchemaVersion != m_identity.SetupSchemaVersion ||
            state.CreatedAt != m_identity.SetupCreatedAt)
        {
            return Stop(
                InstallationSetupOnlyLifecycleStopReason.SetupIdentityChanged,
                state);
        }

        long lastObserved = Interlocked.Read(ref m_lastObservedRevision);
        if (state.Revision < lastObserved)
        {
            return Stop(
                InstallationSetupOnlyLifecycleStopReason.RevisionRegressed,
                state);
        }
        if (state.Revision > lastObserved)
        {
            Interlocked.Exchange(ref m_lastObservedRevision, state.Revision);
        }

        if (state.Lock.Mode == InstallationSetupLockMode.Complete ||
            state.LastCompletedStep == InstallationSetupStep.Administrator)
        {
            return Stop(
                InstallationSetupOnlyLifecycleStopReason.SetupComplete,
                state);
        }

        if (state.Topology is InstallationTopologyKind topology &&
            !InstallationTopologyProfile.For(topology).GatewayRunsHere)
        {
            return Stop(
                InstallationSetupOnlyLifecycleStopReason.GatewayNoLongerRunsHere,
                state);
        }

        return new InstallationSetupOnlyLifecycleDecision(
            InstallationSetupOnlyLifecycleStopReason.None,
            state.Revision,
            state.Lock.Mode,
            state.LastCompletedStep);
    }

    private static InstallationSetupOnlyLifecycleDecision Stop(
        InstallationSetupOnlyLifecycleStopReason reason,
        InstallationSetupState? state = null) =>
        new(
            reason,
            state?.Revision,
            state?.Lock.Mode,
            state?.LastCompletedStep);
}

public sealed class InstallationSetupOnlyLifecycleMonitor : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly InstallationSetupOnlyLifecycleEvaluator m_evaluator;
    private readonly IHostApplicationLifetime m_lifetime;
    private readonly TimeProvider m_timeProvider;
    private readonly ILogger<InstallationSetupOnlyLifecycleMonitor> m_logger;

    public InstallationSetupOnlyLifecycleMonitor(
        InstallationSetupOnlyLifecycleEvaluator evaluator,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider,
        ILogger<InstallationSetupOnlyLifecycleMonitor> logger)
    {
        m_evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        m_lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        m_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        m_logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            InstallationSetupOnlyLifecycleDecision decision;
            try
            {
                decision = await m_evaluator.EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                m_logger.LogCritical(
                    exception,
                    "Setup-only lifecycle evaluation failed; stopping the isolated host.");
                m_lifetime.StopApplication();
                return;
            }

            if (decision.ShouldStop)
            {
                LogStop(decision);
                m_lifetime.StopApplication();
                return;
            }

            await Task.Delay(PollInterval, m_timeProvider, stoppingToken);
        }
    }

    private void LogStop(InstallationSetupOnlyLifecycleDecision decision)
    {
        if (decision.StopReason is
            InstallationSetupOnlyLifecycleStopReason.SetupComplete or
            InstallationSetupOnlyLifecycleStopReason.GatewayNoLongerRunsHere)
        {
            m_logger.LogInformation(
                "Stopping setup-only host because lifecycle state is {StopReason} at revision {SetupRevision}.",
                decision.StopReason,
                decision.SetupRevision);
            return;
        }

        m_logger.LogCritical(
            "Stopping setup-only host fail-closed because lifecycle state is {StopReason} at revision {SetupRevision}.",
            decision.StopReason,
            decision.SetupRevision);
    }
}
