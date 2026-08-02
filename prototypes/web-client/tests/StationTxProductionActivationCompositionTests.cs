using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class StationTxProductionActivationCompositionTests
{
    [Fact]
    public void CompositionTypesKeepAuthorityInsideTheStationBoundary()
    {
        Assert.False(typeof(StationTxProductionActivationComposition).IsPublic);
        Assert.True(
            typeof(StationTxProductionActivationCompositionDiagnostics).IsPublic);

        string[] declaredMethods = typeof(StationTxProductionActivationComposition)
            .GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(["get_Snapshot"], declaredMethods);
    }

    [Fact]
    public void DisabledInputsRemainAttachedButActivationUnavailable()
    {
        StationTxProductionActivationComposition composition = new(
            () => Configuration(requested: false, configured: false),
            () => Inputs(allReady: false));

        StationTxProductionActivationCompositionDiagnostics snapshot =
            composition.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ConfigurationInterlockAttached);
        Assert.True(snapshot.ReadinessEvaluationAttached);
        Assert.False(snapshot.ActivationRequested);
        Assert.True(snapshot.ConfigurationValid);
        Assert.False(snapshot.ActivationAvailable);
        Assert.Equal("activation-not-requested", snapshot.Reason);
        Assert.False(snapshot.Readiness.Ready);
        Assert.Equal("transmit-disabled", snapshot.Readiness.Reason);
    }

    [Fact]
    public void CompleteInfrastructureReportsAvailabilityWithoutAnActionSurface()
    {
        StationTxProductionActivationComposition composition = new(
            () => Configuration(requested: true, configured: true),
            () => Inputs(allReady: true));

        StationTxProductionActivationCompositionDiagnostics snapshot =
            composition.Snapshot;

        Assert.True(snapshot.ActivationAvailable);
        Assert.Equal("ready", snapshot.Reason);
        Assert.True(snapshot.Readiness.Ready);
    }

    [Fact]
    public void SnapshotReevaluatesCurrentInfrastructureInsteadOfCachingAuthority()
    {
        bool ready = false;
        StationTxProductionActivationComposition composition = new(
            () => Configuration(requested: true, configured: true),
            () => Inputs(ready));

        Assert.False(composition.Snapshot.ActivationAvailable);

        ready = true;

        Assert.True(composition.Snapshot.ActivationAvailable);
    }

    [Fact]
    public async Task ProductionLifecyclePublishesAttachedFailClosedComposition()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 8, 2, 12, 30, 0, TimeSpan.Zero));
        await using StationTxProductionLifecycle lifecycle = new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            new TxLeaseManager(time),
            new RadioTxOccupancyRegistry(time),
            NullLogger<StationTxProductionLifecycle>.Instance,
            time);

        StationTxProductionActivationCompositionDiagnostics activation =
            lifecycle.Snapshot.ProductionActivation;

        Assert.True(activation.Registered);
        Assert.True(activation.ConfigurationInterlockAttached);
        Assert.True(activation.ReadinessEvaluationAttached);
        Assert.False(activation.ActivationRequested);
        Assert.True(activation.ConfigurationValid);
        Assert.False(activation.ActivationAvailable);
        Assert.Equal("activation-not-requested", activation.Reason);
        StationTxProductionReadinessDiagnostics nextReadiness =
            lifecycle.Snapshot.ProductionActivation.Readiness;
        Assert.Equal(activation.Readiness.Reason, nextReadiness.Reason);
        Assert.Equal(
            activation.Readiness.MissingPrerequisites,
            nextReadiness.MissingPrerequisites);
        Assert.False(
            lifecycle.Snapshot.BrowserTxTransactionIngress.ExecutionEnabled);
    }

    private static StationTxProductionActivationConfigurationDiagnostics
        Configuration(bool requested, bool configured) =>
        StationTxProductionActivationConfigurationInterlock.Evaluate(new(
            ActivationRequested: requested,
            LocalFlexModeConfigured: configured,
            AllowTransmitConfigured: configured,
            BrowserTxLeaseConfigured: configured,
            CommandTrustVerificationEnabled: configured,
            CommandTrustKeyConfigured: configured,
            CommandSigningEnabled: configured,
            CommandSigningKeyConfigured: configured,
            CommandSubmissionEnabled: configured,
            CommandTransportEnabled: configured,
            CommandTransportAllowlistConfigured: configured,
            EmergencyUnkeyTransportEnabled: configured,
            EmergencyUnkeyTransportAllowlistConfigured: configured,
            WatchdogSupervisionEnabled: configured,
            WatchdogCommandTransportEnabled: configured,
            WatchdogRadioAllowlistConfigured: configured,
            WatchdogArmingEnabled: configured));

    private static StationTxProductionReadinessInputs Inputs(bool allReady) =>
        new(
            AllowTransmitConfigured: allReady,
            BrowserTxLeaseConfigured: allReady,
            CommandCoordinatorAttached: allReady,
            CommandSubmissionEnabled: allReady,
            SigningAvailable: allReady,
            SignatureVerificationAvailable: allReady,
            CommandBoundaryEnabled: allReady,
            CommandAdapterRegistered: allReady,
            GateTransmitEnabled: allReady,
            CommandTransportAvailable: allReady,
            SetTransmitAvailable: allReady,
            EmergencyUnkeyTransportAvailable: allReady,
            SafetyArmAuthorityRegistered: allReady,
            WatchdogSupervisionEnabled: allReady,
            WatchdogProcessRunning: allReady,
            WatchdogIpcConnected: allReady,
            WatchdogCommandTransportAvailable: allReady,
            WatchdogArmingAvailable: allReady);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
