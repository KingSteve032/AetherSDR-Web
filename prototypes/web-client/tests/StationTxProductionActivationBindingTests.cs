using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class StationTxProductionActivationBindingTests
{
    [Fact]
    public void CompletePlanBindsAllFourSwitchesAtomically()
    {
        StationTxProductionActivationBindingDiagnostics binding =
            StationTxProductionActivationBinder.Bind(
                Plan(available: true),
                localFlexSessionEligible: true,
                allowTransmitConfigured: true,
                browserTxLeaseConfigured: true);

        Assert.True(binding.Registered);
        Assert.True(binding.ActivationPlanAttached);
        Assert.True(binding.PlanAvailable);
        Assert.True(binding.SessionEligible);
        Assert.True(binding.BindingApplied);
        Assert.Equal("activation-binding-applied", binding.Reason);
        Assert.True(binding.Binding.CommandBoundaryEnabled);
        Assert.True(binding.Binding.CommandGateTransmitEnabled);
        Assert.True(binding.Binding.BrowserTransactionIngressExecutionEnabled);
        Assert.True(binding.Binding.BrowserKeyingCapabilityEnabled);
    }

    [Theory]
    [InlineData(false, true, true, "local-flex-session-required")]
    [InlineData(true, false, true, "transmit-disabled")]
    [InlineData(true, true, false, "browser-tx-lease-disabled")]
    public void SessionEligibilityFailsClosedWithoutPartialBinding(
        bool localFlex,
        bool allowTransmit,
        bool browserLease,
        string reason)
    {
        StationTxProductionActivationBindingDiagnostics binding =
            StationTxProductionActivationBinder.Bind(
                Plan(available: true),
                localFlex,
                allowTransmit,
                browserLease);

        Assert.False(binding.BindingApplied);
        Assert.Equal(reason, binding.Reason);
        AssertDisabled(binding.Binding);
    }

    [Fact]
    public void UnavailablePlanRemainsUnbound()
    {
        StationTxProductionActivationBindingDiagnostics binding =
            StationTxProductionActivationBinder.Bind(
                Plan(available: false),
                localFlexSessionEligible: true,
                allowTransmitConfigured: true,
                browserTxLeaseConfigured: true);

        Assert.False(binding.PlanAvailable);
        Assert.False(binding.BindingApplied);
        Assert.Equal("activation-not-requested", binding.Reason);
        AssertDisabled(binding.Binding);
    }

    [Fact]
    public void PartialAvailablePlanIsRejectedBeforeRuntimeConstruction()
    {
        StationTxProductionActivationPlanDiagnostics partial =
            Plan(available: true) with
            {
                Plan = new StationTxProductionActivationPlan(
                    CommandBoundaryEnabled: true,
                    CommandGateTransmitEnabled: false,
                    BrowserTransactionIngressExecutionEnabled: true,
                    BrowserKeyingCapabilityEnabled: true)
            };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => StationTxProductionActivationBinder.Bind(
                partial,
                localFlexSessionEligible: true,
                allowTransmitConfigured: true,
                browserTxLeaseConfigured: true));

        Assert.Contains("partial runtime switch set", exception.Message);
    }

    [Fact]
    public async Task LifecycleUsesAppliedBindingForBoundaryGateAndIngress()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 8, 2, 18, 50, 0, TimeSpan.Zero));
        await using StationTxProductionLifecycle lifecycle = new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            new TxLeaseManager(time),
            new RadioTxOccupancyRegistry(time),
            NullLogger<StationTxProductionLifecycle>.Instance,
            time,
            productionReadinessConfiguration: new(
                AllowTransmitConfigured: true,
                BrowserTxLeaseConfigured: true),
            independentWatchdogLocalFlexEligible: true,
            productionActivationConfiguration:
                Configuration(requested: true, configured: true));

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;

        Assert.True(snapshot.ProductionTransmitEnabled);
        Assert.True(snapshot.StationCommandBoundaryEnabled);
        Assert.True(snapshot.BrowserTxTransactionIngress.ExecutionEnabled);
        Assert.Equal("Idle", snapshot.GateState);
        Assert.True(snapshot.ProductionActivation.ActivationBindingApplied);
        Assert.True(snapshot.ProductionActivation.Binding.BindingApplied);
        Assert.False(snapshot.ProductionActivation.ActivationAvailable);
    }

    [Fact]
    public async Task RemoteOrIneligibleSessionCannotApplyGlobalPlan()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 8, 2, 18, 50, 0, TimeSpan.Zero));
        await using StationTxProductionLifecycle lifecycle = new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            new TxLeaseManager(time),
            new RadioTxOccupancyRegistry(time),
            NullLogger<StationTxProductionLifecycle>.Instance,
            time,
            productionReadinessConfiguration: new(
                AllowTransmitConfigured: true,
                BrowserTxLeaseConfigured: true),
            independentWatchdogLocalFlexEligible: false,
            productionActivationConfiguration:
                Configuration(requested: true, configured: true));

        StationTxLifecycleDiagnostics snapshot = lifecycle.Snapshot;

        Assert.False(snapshot.ProductionTransmitEnabled);
        Assert.False(snapshot.StationCommandBoundaryEnabled);
        Assert.False(snapshot.BrowserTxTransactionIngress.ExecutionEnabled);
        Assert.Equal("Disabled", snapshot.GateState);
        Assert.False(snapshot.ProductionActivation.ActivationBindingApplied);
        Assert.Equal(
            "local-flex-session-required",
            snapshot.ProductionActivation.Binding.Reason);
    }

    private static StationTxProductionActivationPlanDiagnostics Plan(
        bool available) =>
        new(
            Registered: true,
            ConfigurationInterlockAttached: true,
            ActivationRequested: available,
            ConfigurationValid: true,
            PlanAvailable: available,
            PlanApplied: false,
            Reason: available
                ? "activation-plan-ready-not-applied"
                : "activation-not-requested",
            Plan: new StationTxProductionActivationPlan(
                CommandBoundaryEnabled: available,
                CommandGateTransmitEnabled: available,
                BrowserTransactionIngressExecutionEnabled: available,
                BrowserKeyingCapabilityEnabled: available));

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

    private static void AssertDisabled(
        StationTxProductionActivationBinding binding)
    {
        Assert.False(binding.CommandBoundaryEnabled);
        Assert.False(binding.CommandGateTransmitEnabled);
        Assert.False(binding.BrowserTransactionIngressExecutionEnabled);
        Assert.False(binding.BrowserKeyingCapabilityEnabled);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
