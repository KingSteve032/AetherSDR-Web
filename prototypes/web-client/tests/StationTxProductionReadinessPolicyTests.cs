using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class StationTxProductionReadinessPolicyTests
{
    private static readonly string[] AllMissing =
    [
        "transmit-disabled",
        "browser-tx-lease-disabled",
        "command-coordinator-unattached",
        "command-submission-disabled",
        "command-signing-unavailable",
        "command-verification-unavailable",
        "command-boundary-disabled",
        "command-adapter-unregistered",
        "command-gate-transmit-disabled",
        "command-transport-unavailable",
        "set-transmit-unavailable",
        "emergency-unkey-transport-unavailable",
        "safety-arm-authority-unregistered",
        "watchdog-supervision-disabled",
        "watchdog-process-unavailable",
        "watchdog-ipc-unavailable",
        "watchdog-unkey-transport-unavailable",
        "watchdog-arming-unavailable"
    ];

    [Fact]
    public void PolicyTypesRemainInsideTheStationBoundary()
    {
        Assert.False(typeof(StationTxProductionReadinessPolicy).IsPublic);
        Assert.False(typeof(StationTxProductionReadinessInputs).IsPublic);
        Assert.False(
            typeof(StationTxProductionReadinessConfiguration).IsPublic);
        Assert.True(typeof(StationTxProductionReadinessDiagnostics).IsPublic);
    }

    [Fact]
    public void DisabledDefaultsReportEveryMissingPrerequisiteInOrder()
    {
        StationTxProductionReadinessDiagnostics result =
            StationTxProductionReadinessPolicy.Evaluate(Inputs(allReady: false));

        Assert.True(result.Registered);
        Assert.False(result.Ready);
        Assert.Equal("transmit-disabled", result.Reason);
        Assert.Equal(AllMissing, result.MissingPrerequisites);
    }

    [Theory]
    [MemberData(nameof(PrerequisiteCases))]
    public void OneMissingPrerequisiteProducesOneExactReason(string reason)
    {
        StationTxProductionReadinessInputs inputs = Missing(reason);

        StationTxProductionReadinessDiagnostics result =
            StationTxProductionReadinessPolicy.Evaluate(inputs);

        Assert.False(result.Ready);
        Assert.Equal(reason, result.Reason);
        Assert.Equal([reason], result.MissingPrerequisites);
    }

    [Fact]
    public void CompleteInfrastructureIsReadyWithoutInventingOperatorAuthority()
    {
        StationTxProductionReadinessDiagnostics result =
            StationTxProductionReadinessPolicy.Evaluate(Inputs(allReady: true));

        Assert.True(result.Registered);
        Assert.True(result.Ready);
        Assert.Equal("ready", result.Reason);
        Assert.Empty(result.MissingPrerequisites);
    }

    [Fact]
    public async Task ProductionLifecyclePublishesFailClosedReadiness()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 8, 1, 23, 30, 0, TimeSpan.Zero));
        await using StationTxProductionLifecycle lifecycle = new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            new TxLeaseManager(time),
            new RadioTxOccupancyRegistry(time),
            NullLogger<StationTxProductionLifecycle>.Instance,
            time);

        StationTxProductionReadinessDiagnostics readiness =
            lifecycle.Snapshot.ProductionReadiness;

        Assert.False(readiness.Ready);
        Assert.Equal("transmit-disabled", readiness.Reason);
        Assert.Contains("command-transport-unavailable", readiness.MissingPrerequisites);
        Assert.Contains(
            "emergency-unkey-transport-unavailable",
            readiness.MissingPrerequisites);
        Assert.Contains(
            "watchdog-unkey-transport-unavailable",
            readiness.MissingPrerequisites);
        Assert.False(
            lifecycle.Snapshot.BrowserTxTransactionIngress.ExecutionEnabled);
    }

    public static TheoryData<string> PrerequisiteCases()
    {
        TheoryData<string> data = [];
        foreach (string reason in AllMissing)
        {
            data.Add(reason);
        }
        return data;
    }

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

    private static StationTxProductionReadinessInputs Missing(string reason)
    {
        StationTxProductionReadinessInputs inputs = Inputs(allReady: true);
        return reason switch
        {
            "transmit-disabled" => inputs with
            {
                AllowTransmitConfigured = false
            },
            "browser-tx-lease-disabled" => inputs with
            {
                BrowserTxLeaseConfigured = false
            },
            "command-coordinator-unattached" => inputs with
            {
                CommandCoordinatorAttached = false
            },
            "command-submission-disabled" => inputs with
            {
                CommandSubmissionEnabled = false
            },
            "command-signing-unavailable" => inputs with
            {
                SigningAvailable = false
            },
            "command-verification-unavailable" => inputs with
            {
                SignatureVerificationAvailable = false
            },
            "command-boundary-disabled" => inputs with
            {
                CommandBoundaryEnabled = false
            },
            "command-adapter-unregistered" => inputs with
            {
                CommandAdapterRegistered = false
            },
            "command-gate-transmit-disabled" => inputs with
            {
                GateTransmitEnabled = false
            },
            "command-transport-unavailable" => inputs with
            {
                CommandTransportAvailable = false
            },
            "set-transmit-unavailable" => inputs with
            {
                SetTransmitAvailable = false
            },
            "emergency-unkey-transport-unavailable" => inputs with
            {
                EmergencyUnkeyTransportAvailable = false
            },
            "safety-arm-authority-unregistered" => inputs with
            {
                SafetyArmAuthorityRegistered = false
            },
            "watchdog-supervision-disabled" => inputs with
            {
                WatchdogSupervisionEnabled = false
            },
            "watchdog-process-unavailable" => inputs with
            {
                WatchdogProcessRunning = false
            },
            "watchdog-ipc-unavailable" => inputs with
            {
                WatchdogIpcConnected = false
            },
            "watchdog-unkey-transport-unavailable" => inputs with
            {
                WatchdogCommandTransportAvailable = false
            },
            "watchdog-arming-unavailable" => inputs with
            {
                WatchdogArmingAvailable = false
            },
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
