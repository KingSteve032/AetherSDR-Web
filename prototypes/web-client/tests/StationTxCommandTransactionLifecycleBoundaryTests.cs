using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandTransactionLifecycleBoundaryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 1, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LifecycleExposesOnlyTypedTransactionOperationsInternally()
    {
        System.Reflection.MethodInfo[] methods =
            typeof(StationTxProductionLifecycle).GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(
            methods,
            method => method.Name == "SubmitValidatedBrowserTxIntentAsync");
        Assert.DoesNotContain(
            methods,
            method => method.ReturnType ==
                typeof(Task<StationTxCommandSessionCompositionResult>));

        AssertTransactionMethod(
            methods,
            "ExecuteStationCommandTransactionAsync",
            typeof(StationTxCommandTransactionRequest));
        AssertTransactionMethod(
            methods,
            "HeartbeatStationCommandTransactionAsync",
            typeof(StationTxCommandTransactionHeartbeatRequest));
        AssertTransactionMethod(
            methods,
            "AbortStationCommandTransactionAsync",
            typeof(StationTxCommandTransactionAbortRequest));

        Assert.DoesNotContain(
            methods.Where(method => method.IsPublic),
            method => method.Name.Contains(
                "StationCommand",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ExternalProductionTypesReceiveNoTransactionBoundaryType()
    {
        Type[] forbiddenTypes =
        [
            typeof(StationTxCommandTransactionComposition),
            typeof(StationTxCommandTransactionRequest),
            typeof(StationTxCommandTransactionHeartbeatRequest),
            typeof(StationTxCommandTransactionAbortRequest),
            typeof(StationTxCommandTransactionResult)
        ];
        Type[] externalTypes =
        [
            typeof(RadioSessionRegistry),
            typeof(RadioCoordinator),
            typeof(RadioWebSocketEndpoint)
        ];

        foreach (Type externalType in externalTypes)
        {
            Assert.DoesNotContain(
                externalType.GetConstructors(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance)
                    .SelectMany(constructor => constructor.GetParameters()),
                parameter => forbiddenTypes.Contains(parameter.ParameterType));
            Assert.DoesNotContain(
                externalType.GetMethods(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.DeclaredOnly)
                    .SelectMany(method => method.GetParameters()),
                parameter => forbiddenTypes.Contains(parameter.ParameterType));
        }
    }

    [Fact]
    public async Task ProductionKeyStopsAtTransactionPreparationWithoutBypass()
    {
        ManualTimeProvider time = new(Start);
        await using StationTxProductionLifecycle lifecycle = Create(time);

        StationTxCommandTransactionResult result =
            await lifecycle.ExecuteStationCommandTransactionAsync(
                TransactionRequest(time, enabled: true));

        Assert.Equal(
            StationTxCommandTransactionOutcome.Rejected,
            result.Outcome);
        Assert.Equal("coordinator_unattached", result.Code);
        Assert.False(result.Success);
        Assert.True(result.OutcomeKnown);
        Assert.Equal(1, result.Diagnostics.AttemptCount);
        Assert.Equal(0, result.Diagnostics.ArmForwardedCount);
        Assert.Equal(0, result.Diagnostics.CommandForwardedCount);
        Assert.Equal(0, result.Diagnostics.HeartbeatForwardedCount);
        Assert.Equal(0, result.Diagnostics.CleanupForwardedCount);
        Assert.False(result.Diagnostics.Active);
        Assert.False(result.Diagnostics.ReconciliationRequired);
        Assert.Equal(
            0,
            lifecycle.Snapshot.StationCommandSessionComposition.AttemptCount);
        Assert.Equal("Disabled", lifecycle.Snapshot.GateState);
        Assert.Equal("Disarmed", lifecycle.Snapshot.SafetyState);
    }

    [Fact]
    public async Task InactiveUnkeyHeartbeatAndAbortNeverReachParticipants()
    {
        ManualTimeProvider time = new(Start);
        await using StationTxProductionLifecycle lifecycle = Create(time);

        StationTxCommandTransactionResult unkey =
            await lifecycle.ExecuteStationCommandTransactionAsync(
                TransactionRequest(time, enabled: false));
        StationTxCommandTransactionResult heartbeat =
            await lifecycle.HeartbeatStationCommandTransactionAsync(
                new StationTxCommandTransactionHeartbeatRequest(
                    "connection-a",
                    TimeSpan.FromSeconds(1)));
        StationTxCommandTransactionResult abort =
            await lifecycle.AbortStationCommandTransactionAsync(
                new StationTxCommandTransactionAbortRequest(
                    "connection-a",
                    "test-abort"));

        Assert.Equal("transaction_inactive", unkey.Code);
        Assert.Equal("transaction_inactive", heartbeat.Code);
        Assert.Equal("transaction_inactive", abort.Code);
        StationTxCommandTransactionCompositionDiagnostics diagnostics =
            lifecycle.Snapshot.StationCommandTransactionComposition;
        Assert.Equal(3, diagnostics.AttemptCount);
        Assert.Equal(0, diagnostics.ArmForwardedCount);
        Assert.Equal(0, diagnostics.CommandForwardedCount);
        Assert.Equal(0, diagnostics.HeartbeatForwardedCount);
        Assert.Equal(0, diagnostics.CleanupForwardedCount);
        Assert.Equal(3, diagnostics.RejectedCount);
        Assert.Equal(0, diagnostics.UnknownCount);
        Assert.False(diagnostics.Active);
        Assert.False(diagnostics.ReconciliationRequired);
    }

    [Fact]
    public async Task PreCancelledLifecycleOperationIsNotCounted()
    {
        ManualTimeProvider time = new(Start);
        await using StationTxProductionLifecycle lifecycle = Create(time);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifecycle.ExecuteStationCommandTransactionAsync(
                TransactionRequest(time, enabled: true),
                cancellation.Token));

        StationTxCommandTransactionCompositionDiagnostics diagnostics =
            lifecycle.Snapshot.StationCommandTransactionComposition;
        Assert.Equal(0, diagnostics.AttemptCount);
        Assert.Equal(0, diagnostics.ArmForwardedCount);
        Assert.Equal(0, diagnostics.CommandForwardedCount);
    }

    private static void AssertTransactionMethod(
        IEnumerable<System.Reflection.MethodInfo> methods,
        string name,
        Type requestType)
    {
        System.Reflection.MethodInfo method = Assert.Single(
            methods,
            candidate => candidate.Name == name);
        Assert.False(method.IsPublic);
        Assert.Equal(
            typeof(Task<StationTxCommandTransactionResult>),
            method.ReturnType);
        Assert.Equal(
            [requestType, typeof(CancellationToken)],
            method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());
    }

    private static StationTxCommandTransactionRequest TransactionRequest(
        TimeProvider time,
        bool enabled) =>
        new(
            "connection-a",
            Sequence: 1,
            new BrowserTxIntent(
                "intent-000000000000000000000000000001",
                BrowserTxIntentKind.Mox,
                "mox.set",
                enabled,
                Text: null),
            time.GetUtcNow(),
            TimeSpan.FromSeconds(1));

    private static StationTxProductionLifecycle Create(
        TimeProvider timeProvider) =>
        new(
            "radio-a",
            "session-a",
            "browser-a",
            "gateway-a",
            new TxLeaseManager(timeProvider),
            new RadioTxOccupancyRegistry(timeProvider),
            NullLogger<StationTxProductionLifecycle>.Instance,
            timeProvider);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
