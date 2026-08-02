using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class StationTxProductionActivationPlanTests
{
    [Fact]
    public void PlannerExposesOnlyAReadOnlySnapshot()
    {
        Assert.False(typeof(StationTxProductionActivationPlanner).IsPublic);
        Assert.True(typeof(StationTxProductionActivationPlan).IsPublic);
        Assert.True(typeof(StationTxProductionActivationPlanDiagnostics).IsPublic);

        string[] methods = typeof(StationTxProductionActivationPlanner)
            .GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(["get_Snapshot"], methods);
        string[] prohibited =
        [
            "activate",
            "apply",
            "execute",
            "submit",
            "key",
            "unkey",
            "arm",
            "lease"
        ];
        Assert.DoesNotContain(
            typeof(StationTxProductionActivationPlanner)
                .GetMethods(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly)
                .Select(method => method.Name),
            method => prohibited.Any(value =>
                method.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void DormantConfigurationProducesAnUnavailableDisabledPlan()
    {
        StationTxProductionActivationPlanner planner = new(
            () => Configuration(requested: false, configured: false));

        StationTxProductionActivationPlanDiagnostics snapshot =
            planner.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ConfigurationInterlockAttached);
        Assert.False(snapshot.ActivationRequested);
        Assert.True(snapshot.ConfigurationValid);
        Assert.False(snapshot.PlanAvailable);
        Assert.False(snapshot.PlanApplied);
        Assert.Equal("activation-not-requested", snapshot.Reason);
        AssertDisabled(snapshot.Plan);
    }

    [Fact]
    public void ValidRequestProducesOneAtomicButUnappliedPlan()
    {
        StationTxProductionActivationPlanner planner = new(
            () => Configuration(requested: true, configured: true));

        StationTxProductionActivationPlanDiagnostics snapshot =
            planner.Snapshot;

        Assert.True(snapshot.ActivationRequested);
        Assert.True(snapshot.ConfigurationValid);
        Assert.True(snapshot.PlanAvailable);
        Assert.False(snapshot.PlanApplied);
        Assert.Equal("activation-plan-ready-not-applied", snapshot.Reason);
        Assert.True(snapshot.Plan.CommandBoundaryEnabled);
        Assert.True(snapshot.Plan.CommandGateTransmitEnabled);
        Assert.True(snapshot.Plan.BrowserTransactionIngressExecutionEnabled);
        Assert.True(snapshot.Plan.BrowserKeyingCapabilityEnabled);
    }

    [Fact]
    public void InvalidRequestNeverProducesAPartialPlan()
    {
        StationTxProductionActivationPlanner planner = new(
            () => Configuration(requested: true, configured: false));

        StationTxProductionActivationPlanDiagnostics snapshot =
            planner.Snapshot;

        Assert.False(snapshot.ConfigurationValid);
        Assert.False(snapshot.PlanAvailable);
        Assert.False(snapshot.PlanApplied);
        Assert.Equal("local-flex-mode-required", snapshot.Reason);
        AssertDisabled(snapshot.Plan);
    }

    [Fact]
    public void SnapshotReevaluatesConfigurationInsteadOfCachingAPlan()
    {
        bool requested = false;
        bool configured = false;
        StationTxProductionActivationPlanner planner = new(
            () => Configuration(requested, configured));

        Assert.False(planner.Snapshot.PlanAvailable);

        requested = true;
        configured = true;

        Assert.True(planner.Snapshot.PlanAvailable);
        Assert.False(planner.Snapshot.PlanApplied);
    }

    [Fact]
    public void CompleteReadinessCannotBypassAnIneligibleBinding()
    {
        StationTxProductionActivationPlanner planner = new(
            () => Configuration(requested: true, configured: true));
        StationTxProductionActivationComposition composition = new(
            () => Configuration(requested: true, configured: true),
            () => planner.Snapshot,
            () => StationTxProductionActivationBinder.Bind(
                planner.Snapshot,
                localFlexSessionEligible: false,
                allowTransmitConfigured: false,
                browserTxLeaseConfigured: false),
            () => Readiness(allReady: true));

        StationTxProductionActivationCompositionDiagnostics snapshot =
            composition.Snapshot;

        Assert.True(snapshot.ActivationPlanAttached);
        Assert.True(snapshot.ActivationPlanAvailable);
        Assert.False(snapshot.ActivationPlanApplied);
        Assert.False(snapshot.ActivationAvailable);
        Assert.Equal("local-flex-session-required", snapshot.Reason);
        Assert.True(snapshot.Readiness.Ready);
    }

    [Fact]
    public async Task LifecyclePublishesAPlanWithoutApplyingIt()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 8, 2, 15, 50, 0, TimeSpan.Zero));
        await using StationTxProductionLifecycle lifecycle = new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            new TxLeaseManager(time),
            new RadioTxOccupancyRegistry(time),
            NullLogger<StationTxProductionLifecycle>.Instance,
            time);

        StationTxLifecycleDiagnostics lifecycleSnapshot = lifecycle.Snapshot;
        StationTxProductionActivationCompositionDiagnostics activation =
            lifecycleSnapshot.ProductionActivation;

        Assert.True(activation.ActivationPlanAttached);
        Assert.False(activation.ActivationPlanAvailable);
        Assert.False(activation.ActivationPlanApplied);
        Assert.Equal("activation-not-requested", activation.Plan.Reason);
        AssertDisabled(activation.Plan.Plan);
        Assert.False(lifecycleSnapshot.StationCommandBoundaryEnabled);
        Assert.False(lifecycleSnapshot.BrowserTxTransactionIngress.ExecutionEnabled);
        Assert.False(lifecycleSnapshot.ProductionTransmitEnabled);
        Assert.Equal("Disabled", lifecycleSnapshot.GateState);
    }

    private static void AssertDisabled(StationTxProductionActivationPlan plan)
    {
        Assert.False(plan.CommandBoundaryEnabled);
        Assert.False(plan.CommandGateTransmitEnabled);
        Assert.False(plan.BrowserTransactionIngressExecutionEnabled);
        Assert.False(plan.BrowserKeyingCapabilityEnabled);
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

    private static StationTxProductionReadinessInputs Readiness(bool allReady) =>
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
