using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class BrowserTxTransactionIngressTests
{
    [Fact]
    public void IngressTypesRemainInternal()
    {
        Assert.False(typeof(BrowserTxTransactionIngress).IsPublic);
        Assert.False(typeof(BrowserTxTransactionIngressRequest).IsPublic);
        Assert.False(typeof(BrowserTxTransactionIngressResult).IsPublic);
    }

    [Fact]
    public void DisabledProductionShapeIsRegisteredButUnavailable()
    {
        Fixture fixture = new(executionEnabled: false);

        BrowserTxTransactionIngressDiagnostics snapshot = fixture.Subject.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.False(snapshot.ExecutionEnabled);
        Assert.True(snapshot.TransactionBoundaryAttached);
        Assert.False(snapshot.KeyAvailable);
        Assert.False(snapshot.UnkeyAvailable);
        Assert.Equal(0, snapshot.AttemptCount);
        Assert.Equal(0, snapshot.ForwardedCount);
        Assert.Equal("execution-disabled", snapshot.LastReason);
    }

    [Fact]
    public async Task DisabledIngressStopsValidIntentBeforeTransactionBoundary()
    {
        Fixture fixture = new(executionEnabled: false);

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(fixture.Request(enabled: true));

        Assert.Equal(BrowserTxTransactionIngressOutcome.Rejected, result.Outcome);
        Assert.Equal("ingress-disabled", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(1, result.Diagnostics.AttemptCount);
        Assert.Equal(0, result.Diagnostics.ForwardedCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidConnectionStopsBeforeForwarding(string connectionClientId)
    {
        Fixture fixture = new();

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(
                fixture.Request(enabled: true) with
                {
                    ConnectionClientId = connectionClientId
                });

        Assert.Equal("invalid-connection-client-id", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Fact]
    public async Task NonIntentRequestStopsBeforeForwarding()
    {
        Fixture fixture = new();
        BrowserTxTransactionIngressRequest request =
            fixture.Request(enabled: true) with
            {
                Request = fixture.Request(enabled: true).Request with
                {
                    Kind = BrowserTxRequestKind.Acquire,
                    Intent = null
                }
            };

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(request);

        Assert.Equal("intent-required", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Fact]
    public async Task UnvalidatedIntentStopsBeforeForwarding()
    {
        Fixture fixture = new();
        BrowserTxTransactionIngressRequest request =
            fixture.Request(enabled: true) with
            {
                Validation = fixture.Request(enabled: true).Validation with
                {
                    Validated = false
                }
            };

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(request);

        Assert.Equal("validation-required", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("outcome")]
    [InlineData("capability")]
    public async Task ValidationMustProveExactServerAuthority(string mutation)
    {
        Fixture fixture = new();
        BrowserTxTransactionIngressRequest original = fixture.Request(enabled: true);
        BrowserTxIntentResult validation = mutation switch
        {
            "ok" => original.Validation with { Ok = true },
            "outcome" => original.Validation with { Outcome = "accepted" },
            "capability" => original.Validation with
            {
                Capability = original.Validation.Capability with
                {
                    IntentValidationAvailable = false
                }
            },
            _ => throw new InvalidOperationException()
        };

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(
                original with { Validation = validation });

        Assert.Equal("validation-not-authoritative", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(2)]
    public async Task StaleOrFutureValidationStopsBeforeForwarding(
        int secondsFromNow)
    {
        Fixture fixture = new();
        BrowserTxTransactionIngressRequest original = fixture.Request(enabled: true);

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(
                original with
                {
                    Validation = original.Validation with
                    {
                        ObservedAt = fixture.Time.GetUtcNow()
                            .AddSeconds(secondsFromNow)
                    }
                });

        Assert.Equal("validation-stale", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("intent")]
    [InlineData("action")]
    public async Task ValidationMismatchStopsBeforeForwarding(string mismatch)
    {
        Fixture fixture = new();
        BrowserTxTransactionIngressRequest original = fixture.Request(enabled: true);
        BrowserTxIntentResult validation = mismatch switch
        {
            "sequence" => original.Validation with
            {
                Sequence = original.Validation.Sequence + 1
            },
            "intent" => original.Validation with
            {
                IntentId = "different-intent"
            },
            "action" => original.Validation with
            {
                Action = "ptt.set"
            },
            _ => throw new InvalidOperationException()
        };

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(
                original with { Validation = validation });

        Assert.Equal("validation-mismatch", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData("Tune", "tune.set")]
    [InlineData("Microphone", "microphone.set")]
    [InlineData("Cw", "cw.send")]
    [InlineData("Mox", "ptt.set")]
    [InlineData("Ptt", "mox.set")]
    public async Task UnsupportedIntentStopsBeforeForwarding(
        string kindName,
        string action)
    {
        Fixture fixture = new();
        BrowserTxIntentKind kind = Enum.Parse<BrowserTxIntentKind>(kindName);
        BrowserTxTransactionIngressRequest original = fixture.Request(enabled: true);
        BrowserTxIntent intent = new(
            original.Request.Intent!.IntentId,
            kind,
            action,
            true,
            kind == BrowserTxIntentKind.Cw ? "CQ" : null);

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(
                original with
                {
                    Request = original.Request with { Intent = intent },
                    Validation = original.Validation with { Action = action }
                });

        Assert.Equal("unsupported-intent", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Fact]
    public async Task MissingBooleanStopsBeforeForwarding()
    {
        Fixture fixture = new();
        BrowserTxTransactionIngressRequest original = fixture.Request(enabled: true);

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(
                original with
                {
                    Request = original.Request with
                    {
                        Intent = original.Request.Intent! with { Enabled = null }
                    }
                });

        Assert.Equal("unsupported-intent", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Fact]
    public async Task KeyUnavailableStopsBeforeForwarding()
    {
        Fixture fixture = new();
        fixture.TransactionSnapshot = Snapshot(
            keyAvailable: false,
            unkeyAvailable: true,
            reason: "authority-stale");

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(fixture.Request(enabled: true));

        Assert.Equal("transaction-key-unavailable", result.Code);
        Assert.Equal("authority-stale", result.Message);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Fact]
    public async Task UnkeyUnavailableStopsBeforeForwarding()
    {
        Fixture fixture = new();
        fixture.TransactionSnapshot = Snapshot(
            keyAvailable: true,
            unkeyAvailable: false,
            reason: "no-active-transaction");

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(fixture.Request(enabled: false));

        Assert.Equal("transaction-unkey-unavailable", result.Code);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData("Mox", "mox.set", true)]
    [InlineData("Mox", "mox.set", false)]
    [InlineData("Ptt", "ptt.set", true)]
    [InlineData("Ptt", "ptt.set", false)]
    public async Task SupportedIntentMapsExactlyOnceWithServerHeartbeat(
        string kindName,
        string action,
        bool enabled)
    {
        Fixture fixture = new();
        BrowserTxIntentKind kind = Enum.Parse<BrowserTxIntentKind>(kindName);
        BrowserTxTransactionIngressRequest request =
            fixture.Request(enabled, kind, action);

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(request);

        Assert.Equal(BrowserTxTransactionIngressOutcome.Accepted, result.Outcome);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.NotNull(fixture.LastRequest);
        Assert.Equal(request.ConnectionClientId, fixture.LastRequest!.ConnectionClientId);
        Assert.Equal(request.Request.Sequence, fixture.LastRequest.Sequence);
        Assert.Same(request.Request.Intent, fixture.LastRequest.Intent);
        Assert.Equal(request.Validation.ObservedAt, fixture.LastRequest.ObservedAt);
        Assert.Equal(
            BrowserTxTransactionIngress.HeartbeatTimeout,
            fixture.LastRequest.HeartbeatTimeout);
        Assert.Equal(1, result.Diagnostics.ForwardedCount);
        Assert.Equal(1, result.Diagnostics.AcceptedCount);
        Assert.Equal(fixture.Time.GetUtcNow(), result.Diagnostics.LastObservedAt);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Unknown")]
    public async Task TransactionOutcomeIsPreservedWithoutRetry(
        string outcomeName)
    {
        Fixture fixture = new();
        StationTxCommandTransactionOutcome outcome =
            Enum.Parse<StationTxCommandTransactionOutcome>(outcomeName);
        fixture.NextOutcome = outcome;

        BrowserTxTransactionIngressResult result =
            await fixture.Subject.SubmitAsync(fixture.Request(enabled: true));

        Assert.Equal(1, fixture.SubmitCalls);
        Assert.Equal(
            outcome == StationTxCommandTransactionOutcome.Unknown
                ? BrowserTxTransactionIngressOutcome.Unknown
                : BrowserTxTransactionIngressOutcome.Rejected,
            result.Outcome);
        Assert.Equal(
            outcome == StationTxCommandTransactionOutcome.Unknown ? 1 : 0,
            result.Diagnostics.UnknownCount);
        Assert.Equal(
            outcome == StationTxCommandTransactionOutcome.Rejected ? 1 : 0,
            result.Diagnostics.RejectedCount);
    }

    [Fact]
    public async Task PreCancelledRequestIsNotCountedOrForwarded()
    {
        Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Subject.SubmitAsync(
                fixture.Request(enabled: true),
                cancellation.Token));

        Assert.Equal(0, fixture.Subject.Snapshot.AttemptCount);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    private sealed class Fixture
    {
        private readonly DateTimeOffset m_observedAt =
            new(2026, 8, 1, 20, 0, 0, TimeSpan.Zero);

        public Fixture(bool executionEnabled = true)
        {
            Time = new ManualTimeProvider(m_observedAt);
            Subject = new BrowserTxTransactionIngress(
                executionEnabled,
                () => TransactionSnapshot,
                SubmitAsync,
                Time);
        }

        public BrowserTxTransactionIngress Subject { get; }

        public ManualTimeProvider Time { get; }

        public StationTxCommandTransactionCompositionDiagnostics
            TransactionSnapshot
        { get; set; } = Snapshot();

        public StationTxCommandTransactionOutcome NextOutcome { get; set; } =
            StationTxCommandTransactionOutcome.Accepted;

        public int SubmitCalls { get; private set; }

        public StationTxCommandTransactionRequest? LastRequest { get; private set; }

        public BrowserTxTransactionIngressRequest Request(
            bool enabled,
            BrowserTxIntentKind kind = BrowserTxIntentKind.Mox,
            string action = "mox.set")
        {
            BrowserTxIntent intent = new(
                "intent-000000000000000000000000000001",
                kind,
                action,
                enabled,
                Text: null);
            BrowserTxRequest request = new(
                RequestId: 1,
                BrowserTxRequestKind.Intent,
                Sequence: 7,
                Seconds: null,
                LeaseId: "lease-1",
                intent);
            BrowserTxIntentResult validation = new(
                Ok: false,
                Validated: true,
                Outcome: "transport-unavailable",
                Error: "Production transport is unavailable.",
                request.Sequence,
                intent.IntentId,
                intent.Action,
                m_observedAt,
                Capability());
            return new BrowserTxTransactionIngressRequest(
                "browser-1",
                request,
                validation);
        }

        private Task<StationTxCommandTransactionResult> SubmitAsync(
            StationTxCommandTransactionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCalls++;
            LastRequest = request;
            return Task.FromResult(new StationTxCommandTransactionResult(
                NextOutcome,
                NextOutcome.ToString().ToLowerInvariant(),
                "transaction-result",
                TransactionSnapshot,
                ArmResult: null,
                CommandResult: null,
                CleanupResult: null));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static BrowserTxCapability Capability() => new(
        RadioBrowserTxProtocol.Version,
        LeaseConfigured: true,
        Authenticated: true,
        RoleAuthorized: true,
        ConnectionCurrent: true,
        RadioConnected: true,
        OccupancyAllowsLease: true,
        LeaseHeldByBrowser: true,
        LeaseAvailable: false,
        IntentValidationAvailable: true,
        KeyingAvailable: false,
        MicrophoneAvailable: false,
        TuneAvailable: false,
        CwAvailable: false,
        State: "intent-validation-ready",
        Message: "Validated only.");

    private static StationTxCommandTransactionCompositionDiagnostics Snapshot(
        bool keyAvailable = true,
        bool unkeyAvailable = true,
        string reason = "ready") => new(
        Registered: true,
        SafetyArmCompositionAttached: true,
        CommandSessionCompositionAttached: true,
        AuthoritySnapshotAvailable: true,
        keyAvailable,
        HeartbeatAvailable: true,
        unkeyAvailable,
        AbortAvailable: true,
        Active: false,
        ReconciliationRequired: false,
        State: "idle",
        AttemptCount: 0,
        ArmForwardedCount: 0,
        CommandForwardedCount: 0,
        HeartbeatForwardedCount: 0,
        CleanupForwardedCount: 0,
        AcceptedCount: 0,
        RejectedCount: 0,
        UnknownCount: 0,
        LastOperation: "none",
        LastOutcome: "none",
        LastObservedAt: null,
        reason);
}
