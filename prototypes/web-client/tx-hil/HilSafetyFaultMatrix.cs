using AetherSDR.Web.Radio;

namespace AetherSDR.TxHil;

internal sealed record HilSafetyFaultScenario(
    string Name,
    bool Passed,
    string FinalCode,
    StationTxSafetyState FinalState,
    int UnkeyCommands,
    string Detail);

internal static class HilSafetyFaultMatrix
{
    private const string RadioId = "flex:simulated-safety-radio";
    private const string EngineInstanceId = "simulated-engine";
    private const string LeaseId = "simulated-lease";
    private const string SessionId = "simulated-session";
    private const string BrowserClientId = "simulated-browser";
    private const uint ProtectedHandle = 0x10203040;
    private const uint ObserverHandle = 0x50607080;
    private const uint SmartSdrHandle = 0x90A0B0C0;

    public static async Task<IReadOnlyList<HilSafetyFaultScenario>> RunAsync(
        CancellationToken cancellationToken)
    {
        List<HilSafetyFaultScenario> results = [];
        results.Add(await HeartbeatExpiryIdleAsync(cancellationToken));
        results.Add(await HeartbeatExpiryProtectedTxAsync(cancellationToken));
        results.Add(await BrowserLossAsync(cancellationToken));
        results.Add(await ExternalOwnerProtectionAsync(cancellationToken));
        results.Add(await UnknownUnkeyOutcomeAsync(cancellationToken));
        results.Add(await TransportOutageRecoveryAsync(cancellationToken));
        results.Add(await StartupReconciliationAsync(cancellationToken));
        results.Add(await BoundedRetryExhaustionAsync(cancellationToken));
        return results;
    }

    private static async Task<HilSafetyFaultScenario> HeartbeatExpiryIdleAsync(
        CancellationToken cancellationToken)
    {
        MatrixFixture fixture = NewFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await RequireAsync(
            supervisor.ArmAsync(Arm(TimeSpan.FromSeconds(1)), cancellationToken),
            "arm");
        fixture.Time.Advance(TimeSpan.FromSeconds(1.1));
        ObserveIdle(fixture, ProtectedHandle);
        StationTxSafetyResult result = await supervisor.EvaluateAsync(
            "fault-matrix",
            cancellationToken);
        bool passed =
            result.Success &&
            result.Code == "disarmed" &&
            fixture.Transport.CommandCount == 0;
        return Scenario(
            "heartbeat-expiry-idle",
            passed,
            result,
            fixture,
            "Idle expiry must disarm without a global unkey.");
    }

    private static async Task<HilSafetyFaultScenario>
        HeartbeatExpiryProtectedTxAsync(
            CancellationToken cancellationToken)
    {
        MatrixFixture fixture = NewFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await RequireAsync(
            supervisor.ArmAsync(Arm(TimeSpan.FromSeconds(1)), cancellationToken),
            "arm");
        fixture.Time.Advance(TimeSpan.FromSeconds(1.1));
        ObserveProtectedTx(fixture);
        StationTxSafetyResult result = await supervisor.EvaluateAsync(
            "fault-matrix",
            cancellationToken);
        bool passed =
            result.Success &&
            result.Code == "unkey_pending" &&
            result.Snapshot.SawProtectedTransmit &&
            fixture.Transport.CommandCount == 1;
        return Scenario(
            "heartbeat-expiry-protected-tx",
            passed,
            result,
            fixture,
            "The exact armed FLEX handle must receive one emergency unkey.");
    }

    private static async Task<HilSafetyFaultScenario> BrowserLossAsync(
        CancellationToken cancellationToken)
    {
        MatrixFixture fixture = NewFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await RequireAsync(
            supervisor.ArmAsync(Arm(), cancellationToken),
            "arm");
        ObserveProtectedTx(fixture);
        StationTxSafetyResult result = await supervisor.AbortAsync(
            "browser-disconnected",
            cancellationToken);
        bool passed =
            result.Success &&
            result.Code == "unkey_pending" &&
            fixture.Transport.CommandCount == 1;
        return Scenario(
            "browser-loss",
            passed,
            result,
            fixture,
            "An explicit owner-loss signal must use the same exact-owner unkey path.");
    }

    private static async Task<HilSafetyFaultScenario>
        ExternalOwnerProtectionAsync(
            CancellationToken cancellationToken)
    {
        MatrixFixture fixture = NewFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await RequireAsync(
            supervisor.ArmAsync(Arm(), cancellationToken),
            "arm");
        ObserveExternalTx(fixture);
        StationTxSafetyResult result = await supervisor.AbortAsync(
            "gateway-link-lost",
            cancellationToken);
        bool passed =
            !result.Success &&
            result.Code == "external_tx_owner" &&
            fixture.Transport.CommandCount == 0;
        return Scenario(
            "external-owner-protection",
            passed,
            result,
            fixture,
            "SmartSDR or another non-matching handle must never receive global unkey.");
    }

    private static async Task<HilSafetyFaultScenario> UnknownUnkeyOutcomeAsync(
        CancellationToken cancellationToken)
    {
        MatrixFixture fixture = NewFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        fixture.Transport.Results.Enqueue(
            StationTxTransportResult.Unknown("simulated socket loss"));
        ObserveIdle(fixture, ProtectedHandle);
        await RequireAsync(
            supervisor.ArmAsync(Arm(), cancellationToken),
            "arm");
        ObserveProtectedTx(fixture);
        StationTxSafetyResult unknown = await supervisor.AbortAsync(
            "lease-expired",
            cancellationToken);
        fixture.Time.Advance(
            StationTxSafetySupervisor.UnkeyConfirmationTimeout +
            TimeSpan.FromMilliseconds(1));
        ObserveProtectedTx(fixture);
        StationTxSafetyResult retry = await supervisor.EvaluateAsync(
            "fault-matrix",
            cancellationToken);
        bool passed =
            !unknown.Success &&
            unknown.Code == "emergency_unkey_outcome_unknown" &&
            retry.Success &&
            retry.Code == "unkey_pending" &&
            fixture.Transport.CommandCount == 2;
        return Scenario(
            "unknown-unkey-outcome",
            passed,
            retry,
            fixture,
            "An unknown socket result must retain guarded intent and retry only while exact ownership remains.");
    }

    private static async Task<HilSafetyFaultScenario>
        TransportOutageRecoveryAsync(
            CancellationToken cancellationToken)
    {
        MatrixFixture fixture = NewFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await RequireAsync(
            supervisor.ArmAsync(Arm(), cancellationToken),
            "arm");
        ObserveProtectedTx(fixture);
        fixture.Transport.IsConnected = false;
        StationTxSafetyResult unavailable = await supervisor.AbortAsync(
            "engine-shutdown",
            cancellationToken);
        fixture.Transport.IsConnected = true;
        fixture.Time.Advance(
            StationTxSafetySupervisor.TransportRetryInterval +
            TimeSpan.FromMilliseconds(1));
        ObserveProtectedTx(fixture);
        StationTxSafetyResult retry = await supervisor.EvaluateAsync(
            "fault-matrix",
            cancellationToken);
        bool passed =
            !unavailable.Success &&
            unavailable.Code == "emergency_transport_unavailable" &&
            retry.Success &&
            fixture.Transport.CommandCount == 1;
        return Scenario(
            "transport-outage-recovery",
            passed,
            retry,
            fixture,
            "A temporary observer transport outage must not discard the protected arm.");
    }

    private static async Task<HilSafetyFaultScenario> StartupReconciliationAsync(
        CancellationToken cancellationToken)
    {
        MatrixFixture fixture = NewFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveProtectedTx(fixture);
        StationTxSafetyResult result = await supervisor.EvaluateAsync(
            "startup-reconciliation",
            cancellationToken);
        bool passed =
            result.Success &&
            result.Code == "disarmed" &&
            fixture.Transport.CommandCount == 0;
        return Scenario(
            "startup-reconciliation",
            passed,
            result,
            fixture,
            "A newly started watchdog must never invent ownership of an existing transmission.");
    }

    private static async Task<HilSafetyFaultScenario>
        BoundedRetryExhaustionAsync(
            CancellationToken cancellationToken)
    {
        MatrixFixture fixture = NewFixture();
        await using StationTxSafetySupervisor supervisor = fixture.Supervisor;
        ObserveIdle(fixture, ProtectedHandle);
        await RequireAsync(
            supervisor.ArmAsync(Arm(), cancellationToken),
            "arm");
        ObserveProtectedTx(fixture);
        await supervisor.AbortAsync("lease-expired", cancellationToken);
        for (int attempt = 2;
             attempt <= StationTxSafetySupervisor.MaximumUnkeyAttempts;
             attempt++)
        {
            fixture.Time.Advance(
                StationTxSafetySupervisor.UnkeyConfirmationTimeout +
                TimeSpan.FromMilliseconds(1));
            ObserveProtectedTx(fixture);
            await supervisor.EvaluateAsync(
                "fault-matrix",
                cancellationToken);
        }
        fixture.Time.Advance(
            StationTxSafetySupervisor.UnkeyConfirmationTimeout +
            TimeSpan.FromMilliseconds(1));
        ObserveProtectedTx(fixture);
        StationTxSafetyResult exhausted = await supervisor.EvaluateAsync(
            "fault-matrix",
            cancellationToken);
        bool passed =
            !exhausted.Success &&
            exhausted.Code == "unkey_confirmation_timeout" &&
            fixture.Transport.CommandCount ==
                StationTxSafetySupervisor.MaximumUnkeyAttempts;
        return Scenario(
            "bounded-retry-exhaustion",
            passed,
            exhausted,
            fixture,
            "Emergency unkey attempts must be bounded and leave a visible fault.");
    }

    private static HilSafetyFaultScenario Scenario(
        string name,
        bool passed,
        StationTxSafetyResult result,
        MatrixFixture fixture,
        string detail) =>
        new(
            name,
            passed,
            result.Code,
            result.Snapshot.State,
            fixture.Transport.CommandCount,
            detail);

    private static async Task RequireAsync(
        Task<StationTxSafetyResult> operation,
        string step)
    {
        StationTxSafetyResult result = await operation;
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Fault matrix {step} failed: {result.Code}: {result.Message}");
        }
    }

    private static MatrixFixture NewFixture()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero));
        RadioTxOccupancyRegistry occupancy = new(time);
        FakeEmergencyTransport transport = new();
        StationTxSafetySupervisor supervisor = new(
            RadioId,
            occupancy,
            transport,
            time);
        return new MatrixFixture(time, occupancy, transport, supervisor);
    }

    private static StationTxSafetyArm Arm(
        TimeSpan? heartbeatTimeout = null) =>
        new(
            EngineInstanceId,
            LeaseId,
            SessionId,
            BrowserClientId,
            ProtectedHandle,
            heartbeatTimeout ?? TimeSpan.FromSeconds(2));

    private static void ObserveIdle(
        MatrixFixture fixture,
        uint localPttHandle)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "READY",
            null,
            null,
            Clients(localPttHandle));
    }

    private static void ObserveProtectedTx(MatrixFixture fixture)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            ProtectedHandle,
            "SW",
            Clients(ProtectedHandle));
    }

    private static void ObserveExternalTx(MatrixFixture fixture)
    {
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            SmartSdrHandle,
            "SW",
            Clients(SmartSdrHandle));
    }

    private static RadioGuiClientDiagnostics[] Clients(uint localPttHandle) =>
    [
        new RadioGuiClientDiagnostics(
            ProtectedHandle,
            "engine",
            "AetherD",
            "AETHER-ENGINE",
            string.Empty,
            localPttHandle == ProtectedHandle,
            false),
        new RadioGuiClientDiagnostics(
            ObserverHandle,
            "observer",
            "AetherSDR Safety",
            "AETHER-SAFETY",
            string.Empty,
            localPttHandle == ObserverHandle,
            true),
        new RadioGuiClientDiagnostics(
            SmartSdrHandle,
            "external",
            "SmartSDR-Win",
            "STEVENS-SURFACE",
            string.Empty,
            localPttHandle == SmartSdrHandle,
            false)
    ];

    private sealed record MatrixFixture(
        ManualTimeProvider Time,
        RadioTxOccupancyRegistry Occupancy,
        FakeEmergencyTransport Transport,
        StationTxSafetySupervisor Supervisor);

    private sealed class FakeEmergencyTransport
        : IStationTxEmergencyUnkeyTransport
    {
        public bool IsConnected { get; set; } = true;
        public int CommandCount { get; private set; }
        public Queue<StationTxTransportResult> Results { get; } = new();

        public Task<StationTxTransportResult> RequestUnkeyAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandCount++;
            return Task.FromResult(
                Results.Count > 0
                    ? Results.Dequeue()
                    : StationTxTransportResult.Ok);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration) =>
            m_now = m_now.Add(duration);
    }
}
