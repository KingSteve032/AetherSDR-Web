using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandTransactionCompositionTests
{
    [Fact]
    public void TransactionTypesRemainPrivateToTheStationBoundary()
    {
        Assert.False(typeof(StationTxCommandTransactionComposition).IsPublic);
        Assert.False(
            typeof(IStationTxSafetyArmTransactionParticipant).IsPublic);
        Assert.False(
            typeof(IStationTxCommandTransactionSubmissionParticipant).IsPublic);
        Assert.DoesNotContain(
            typeof(StationTxCommandTransactionComposition).GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly),
            method => method.Name is "SubmitAsync" or "HeartbeatAsync" or
                "AbortAsync");
    }

    [Fact]
    public void SnapshotFailsClosedWhenParticipantsAreUnattached()
    {
        Fixture fixture = new(
            safetyAttached: false,
            commandAttached: false);

        StationTxCommandTransactionCompositionDiagnostics snapshot =
            fixture.Subject.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.False(snapshot.SafetyArmCompositionAttached);
        Assert.False(snapshot.CommandSessionCompositionAttached);
        Assert.True(snapshot.AuthoritySnapshotAvailable);
        Assert.False(snapshot.KeyAvailable);
        Assert.False(snapshot.HeartbeatAvailable);
        Assert.False(snapshot.UnkeyAvailable);
        Assert.False(snapshot.AbortAvailable);
        Assert.False(snapshot.Active);
        Assert.False(snapshot.ReconciliationRequired);
        Assert.Equal("idle", snapshot.State);
        Assert.Equal("safety-arm-composition-unattached", snapshot.Reason);
    }

    [Fact]
    public void SnapshotSeparatesRegistrationFromAvailability()
    {
        Fixture fixture = new();
        fixture.Command.SubmissionEnabled = false;
        fixture.Safety.ArmAvailableOverride = false;

        StationTxCommandTransactionCompositionDiagnostics snapshot =
            fixture.Subject.Snapshot;

        Assert.True(snapshot.SafetyArmCompositionAttached);
        Assert.True(snapshot.CommandSessionCompositionAttached);
        Assert.True(snapshot.AuthoritySnapshotAvailable);
        Assert.False(snapshot.KeyAvailable);
        Assert.Equal("submission-disabled", snapshot.Reason);
        Assert.Equal(0, snapshot.AttemptCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\u0001bad")]
    public async Task InvalidConnectionStopsBeforeEveryParticipant(string connection)
    {
        Fixture fixture = new();

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true) with
            {
                ConnectionClientId = connection
            });

        Assert.Equal(
            StationTxCommandTransactionOutcome.Rejected,
            result.Outcome);
        Assert.Equal("invalid_connection_client_id", result.Code);
        Assert.Equal(0, fixture.Safety.ArmCalls);
        Assert.Equal(0, fixture.Command.SubmitCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9007199254740992)]
    public async Task InvalidSequenceStopsBeforeArming(long sequence)
    {
        Fixture fixture = new();

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true) with { Sequence = sequence });

        Assert.Equal("invalid_intent_sequence", result.Code);
        Assert.Equal(0, fixture.Safety.ArmCalls);
        Assert.Equal(0, fixture.Command.SubmitCalls);
    }

    [Fact]
    public async Task UnsupportedIntentStopsBeforeArming()
    {
        Fixture fixture = new();
        StationTxCommandTransactionRequest request =
            fixture.Request(enabled: true) with
            {
                Intent = new BrowserTxIntent(
                    "intent-a",
                    BrowserTxIntentKind.Tune,
                    "tune.set",
                    true,
                    null)
            };

        StationTxCommandTransactionResult result =
            await fixture.Subject.SubmitAsync(request);

        Assert.Equal("unsupported_intent", result.Code);
        Assert.Equal(0, fixture.Safety.ArmCalls);
    }

    [Fact]
    public async Task MissingBooleanStopsBeforeArming()
    {
        Fixture fixture = new();
        StationTxCommandTransactionRequest request =
            fixture.Request(enabled: true) with
            {
                Intent = fixture.Request(enabled: true).Intent with
                {
                    Enabled = null
                }
            };

        StationTxCommandTransactionResult result =
            await fixture.Subject.SubmitAsync(request);

        Assert.Equal("missing_intent_value", result.Code);
        Assert.Equal(0, fixture.Safety.ArmCalls);
    }

    [Fact]
    public async Task DisabledSubmissionStopsBeforeArming()
    {
        Fixture fixture = new();
        fixture.Command.SubmissionEnabled = false;

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.Equal("submission_disabled", result.Code);
        Assert.Equal(0, fixture.Safety.ArmCalls);
        Assert.Equal(0, fixture.Command.SubmitCalls);
    }

    [Fact]
    public async Task UnavailableSafetyArmStopsBeforeCommand()
    {
        Fixture fixture = new();
        fixture.Safety.ArmAvailableOverride = false;
        fixture.Safety.ReasonOverride = "boundary-disabled";

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.Equal("boundary_disabled", result.Code);
        Assert.Equal(0, fixture.Safety.ArmCalls);
        Assert.Equal(0, fixture.Command.SubmitCalls);
    }

    [Fact]
    public async Task AuthorityResolutionFailureStopsBeforeArming()
    {
        Fixture fixture = new();
        fixture.AuthorityFailure = StationTxCommandAuthorityResolution.Rejected(
            "authority-stale",
            "stale");

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.Equal("authority-stale", result.Code);
        Assert.Equal(0, fixture.Safety.ArmCalls);
    }

    [Fact]
    public async Task ArmRejectionClearsProvisionalTransaction()
    {
        Fixture fixture = new();
        fixture.Safety.ArmResult = fixture.SafetyResult(
            success: false,
            "arm_rejected");

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.Equal(
            StationTxCommandTransactionOutcome.Rejected,
            result.Outcome);
        Assert.Equal("arm_rejected", result.Code);
        Assert.Equal(1, fixture.Safety.ArmCalls);
        Assert.Equal(0, fixture.Command.SubmitCalls);
        Assert.False(result.Diagnostics.Active);
        Assert.Equal("idle", result.Diagnostics.State);
    }

    [Fact]
    public async Task ExactKeyTransactionArmsAndSubmitsOnlyOnce()
    {
        Fixture fixture = new();

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.True(result.Success);
        Assert.Equal("key_accepted", result.Code);
        Assert.Equal(1, fixture.Safety.ArmCalls);
        Assert.Equal(1, fixture.Command.SubmitCalls);
        Assert.True(fixture.Command.Requests.Single().Intent.Enabled);
        Assert.True(result.Diagnostics.Active);
        Assert.False(result.Diagnostics.ReconciliationRequired);
        Assert.Equal("armed", result.Diagnostics.State);
        Assert.Equal(1, result.Diagnostics.ArmForwardedCount);
        Assert.Equal(1, result.Diagnostics.CommandForwardedCount);
    }

    [Fact]
    public async Task SecondKeyIsRejectedWithoutAnotherArmOrCommand()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();

        StationTxCommandTransactionResult second =
            await fixture.Subject.SubmitAsync(
                fixture.Request(enabled: true, sequence: 2));

        Assert.Equal("transaction_active", second.Code);
        Assert.Equal(1, fixture.Safety.ArmCalls);
        Assert.Equal(1, fixture.Command.SubmitCalls);
    }

    [Fact]
    public async Task AuthorityChangeAfterArmAbortsWithoutSubmitting()
    {
        Fixture fixture = new();
        fixture.Safety.AfterArm = () =>
        {
            fixture.Authority = fixture.Authority with
            {
                EngineInstanceId = "engine-replaced"
            };
        };

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.Equal("authority_changed_before_submit", result.Code);
        Assert.Equal(1, fixture.Safety.ArmCalls);
        Assert.Equal(1, fixture.Safety.AbortCalls);
        Assert.Equal(0, fixture.Command.SubmitCalls);
        Assert.False(result.Diagnostics.Active);
    }

    [Fact]
    public async Task CommandPathLossAfterArmAbortsWithoutSubmitting()
    {
        Fixture fixture = new();
        fixture.Command.AvailableAfterArm = false;

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.Equal("arming_unavailable", result.Code);
        Assert.Equal(1, fixture.Safety.ArmCalls);
        Assert.Equal(1, fixture.Safety.AbortCalls);
        Assert.Equal(0, fixture.Command.SubmitCalls);
        Assert.False(result.Diagnostics.Active);
    }

    [Fact]
    public async Task KnownKeyRejectionClearsTheMatchingArm()
    {
        Fixture fixture = new();
        fixture.Command.KeyResult = fixture.CommandResult(
            success: false,
            "adapter_rejected");

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.Equal(
            StationTxCommandTransactionOutcome.Rejected,
            result.Outcome);
        Assert.Equal("adapter_rejected", result.Code);
        Assert.Equal(1, fixture.Command.SubmitCalls);
        Assert.Equal(1, fixture.Safety.AbortCalls);
        Assert.False(result.Diagnostics.Active);
        Assert.False(result.Diagnostics.ReconciliationRequired);
    }

    [Fact]
    public async Task FailedCleanupAfterKnownKeyRejectionRequiresReconciliation()
    {
        Fixture fixture = new();
        fixture.Command.KeyResult = fixture.CommandResult(
            success: false,
            "adapter_rejected");
        fixture.Safety.AbortResult = fixture.SafetyResult(
            success: false,
            "abort_rejected");

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.Equal(
            StationTxCommandTransactionOutcome.Rejected,
            result.Outcome);
        Assert.True(result.Diagnostics.Active);
        Assert.True(result.Diagnostics.ReconciliationRequired);
        Assert.Equal("reconciling", result.Diagnostics.State);
        Assert.Contains("cleanup requires reconciliation", result.Message);
    }

    [Fact]
    public async Task UnknownKeyOutcomeRetainsArmWithoutCleanupOrRetry()
    {
        Fixture fixture = new();
        fixture.Command.KeyResult = fixture.CommandResult(
            success: false,
            "adapter_outcome_unknown");

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: true));

        Assert.Equal(
            StationTxCommandTransactionOutcome.Unknown,
            result.Outcome);
        Assert.False(result.OutcomeKnown);
        Assert.Equal(1, fixture.Safety.ArmCalls);
        Assert.Equal(1, fixture.Command.SubmitCalls);
        Assert.Equal(0, fixture.Safety.AbortCalls);
        Assert.True(result.Diagnostics.Active);
        Assert.True(result.Diagnostics.ReconciliationRequired);
        Assert.Equal(1, result.Diagnostics.UnknownCount);
    }

    [Fact]
    public async Task KeySubmissionExceptionRetainsArmAndPropagates()
    {
        Fixture fixture = new();
        fixture.Command.SubmitException = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Subject.SubmitAsync(fixture.Request(enabled: true)));

        StationTxCommandTransactionCompositionDiagnostics snapshot =
            fixture.Subject.Snapshot;
        Assert.True(snapshot.Active);
        Assert.True(snapshot.ReconciliationRequired);
        Assert.Equal(1, snapshot.UnknownCount);
        Assert.Equal(0, fixture.Safety.AbortCalls);
        Assert.Equal(1, fixture.Command.SubmitCalls);
    }

    [Fact]
    public async Task HeartbeatRequiresAnActiveExactConnection()
    {
        Fixture fixture = new();

        StationTxCommandTransactionResult inactive =
            await fixture.Subject.HeartbeatAsync(
                fixture.HeartbeatRequest());
        Assert.Equal("transaction_inactive", inactive.Code);

        await fixture.ArmKeyAsync();
        StationTxCommandTransactionResult mismatch =
            await fixture.Subject.HeartbeatAsync(
                fixture.HeartbeatRequest() with
                {
                    ConnectionClientId = "connection-b"
                });
        Assert.Equal("connection_mismatch", mismatch.Code);
        Assert.Equal(0, fixture.Safety.HeartbeatCalls);
    }

    [Fact]
    public async Task ExactHeartbeatIsForwardedOnceAndKeepsTheArm()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();

        StationTxCommandTransactionResult result =
            await fixture.Subject.HeartbeatAsync(fixture.HeartbeatRequest());

        Assert.True(result.Success);
        Assert.Equal("heartbeat_accepted", result.Code);
        Assert.Equal(1, fixture.Safety.HeartbeatCalls);
        Assert.True(result.Diagnostics.Active);
        Assert.Equal("armed", result.Diagnostics.State);
    }

    [Fact]
    public async Task ActiveAuthorityAcceptsMonotonicLeaseExtension()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();
        fixture.Authority = fixture.Authority with
        {
            LeaseExpiresAt = fixture.Authority.LeaseExpiresAt.AddSeconds(10)
        };

        StationTxCommandTransactionResult result =
            await fixture.Subject.HeartbeatAsync(fixture.HeartbeatRequest());

        Assert.True(result.Success);
        Assert.Equal("heartbeat_accepted", result.Code);
        Assert.Equal(1, fixture.Safety.HeartbeatCalls);
        Assert.True(result.Diagnostics.Active);
    }

    [Fact]
    public async Task ActiveAuthorityRejectsLeaseExpiryShortening()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();
        fixture.Authority = fixture.Authority with
        {
            LeaseExpiresAt = fixture.Authority.LeaseExpiresAt.AddSeconds(-1)
        };

        StationTxCommandTransactionResult result =
            await fixture.Subject.HeartbeatAsync(fixture.HeartbeatRequest());

        Assert.False(result.Success);
        Assert.Equal("active_authority_changed", result.Code);
        Assert.Equal(0, fixture.Safety.HeartbeatCalls);
        Assert.True(result.Diagnostics.Active);
    }

    [Fact]
    public async Task RejectedHeartbeatRequiresReconciliation()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();
        fixture.Safety.HeartbeatResult = fixture.SafetyResult(
            success: false,
            "heartbeat_rejected");

        StationTxCommandTransactionResult result =
            await fixture.Subject.HeartbeatAsync(fixture.HeartbeatRequest());

        Assert.Equal("heartbeat_rejected", result.Code);
        Assert.True(result.Diagnostics.Active);
        Assert.True(result.Diagnostics.ReconciliationRequired);
    }

    [Fact]
    public async Task UnkeyRequiresAnActiveTransaction()
    {
        Fixture fixture = new();

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: false));

        Assert.Equal("transaction_inactive", result.Code);
        Assert.Equal(0, fixture.Safety.HeartbeatCalls);
        Assert.Equal(0, fixture.Command.SubmitCalls);
    }

    [Fact]
    public async Task ExactUnkeyHeartbeatsSubmitsAndClearsTheArm()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: false, sequence: 2));

        Assert.True(result.Success);
        Assert.Equal("unkey_accepted", result.Code);
        Assert.Equal(1, fixture.Safety.HeartbeatCalls);
        Assert.Equal(2, fixture.Command.SubmitCalls);
        Assert.False(fixture.Command.Requests.Last().Intent.Enabled);
        Assert.Equal(1, fixture.Safety.AbortCalls);
        Assert.False(result.Diagnostics.Active);
        Assert.Equal("idle", result.Diagnostics.State);
    }

    [Fact]
    public async Task KnownUnkeyRejectionRetainsTheActiveArm()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();
        fixture.Command.UnkeyResult = fixture.CommandResult(
            success: false,
            "adapter_rejected");

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: false, sequence: 2));

        Assert.Equal(
            StationTxCommandTransactionOutcome.Rejected,
            result.Outcome);
        Assert.Equal(1, fixture.Safety.HeartbeatCalls);
        Assert.Equal(0, fixture.Safety.AbortCalls);
        Assert.True(result.Diagnostics.Active);
        Assert.False(result.Diagnostics.ReconciliationRequired);
        Assert.Equal("armed", result.Diagnostics.State);
    }

    [Fact]
    public async Task UnknownUnkeyOutcomeRetainsArmForReconciliation()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();
        fixture.Command.UnkeyResult = fixture.CommandResult(
            success: false,
            "adapter_outcome_unknown");

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: false, sequence: 2));

        Assert.Equal(
            StationTxCommandTransactionOutcome.Unknown,
            result.Outcome);
        Assert.Equal(0, fixture.Safety.AbortCalls);
        Assert.True(result.Diagnostics.Active);
        Assert.True(result.Diagnostics.ReconciliationRequired);
    }

    [Fact]
    public async Task UnkeyCleanupFailureBecomesUnknownAndRetainsArm()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();
        fixture.Safety.AbortResult = fixture.SafetyResult(
            success: false,
            "abort_rejected");

        StationTxCommandTransactionResult result = await fixture.Subject.SubmitAsync(
            fixture.Request(enabled: false, sequence: 2));

        Assert.Equal(
            StationTxCommandTransactionOutcome.Unknown,
            result.Outcome);
        Assert.Equal("abort_rejected", result.Code);
        Assert.True(result.Diagnostics.Active);
        Assert.True(result.Diagnostics.ReconciliationRequired);
    }

    [Fact]
    public async Task ExplicitAbortClearsOnlyTheActiveExactTransaction()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();

        StationTxCommandTransactionResult result =
            await fixture.Subject.AbortAsync(fixture.AbortRequest());

        Assert.True(result.Success);
        Assert.Equal("abort_accepted", result.Code);
        Assert.Equal(1, fixture.Safety.AbortCalls);
        Assert.False(result.Diagnostics.Active);
    }

    [Fact]
    public async Task RejectedAbortRetainsArmForReconciliation()
    {
        Fixture fixture = new();
        await fixture.ArmKeyAsync();
        fixture.Safety.AbortResult = fixture.SafetyResult(
            success: false,
            "abort_rejected");

        StationTxCommandTransactionResult result =
            await fixture.Subject.AbortAsync(fixture.AbortRequest());

        Assert.Equal("abort_rejected", result.Code);
        Assert.True(result.Diagnostics.Active);
        Assert.True(result.Diagnostics.ReconciliationRequired);
    }

    [Fact]
    public async Task CancellationBeforeOperationDoesNotCreateAnAttempt()
    {
        Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Subject.SubmitAsync(
                fixture.Request(enabled: true),
                cancellation.Token));

        Assert.Equal(0, fixture.Subject.Snapshot.AttemptCount);
        Assert.Equal(0, fixture.Safety.ArmCalls);
    }

    [Fact]
    public async Task ArmCancellationIsUnknownAndNeverRetries()
    {
        Fixture fixture = new();
        fixture.Safety.ArmException = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Subject.SubmitAsync(fixture.Request(enabled: true)));

        StationTxCommandTransactionCompositionDiagnostics snapshot =
            fixture.Subject.Snapshot;
        Assert.Equal(1, fixture.Safety.ArmCalls);
        Assert.Equal(0, fixture.Command.SubmitCalls);
        Assert.True(snapshot.Active);
        Assert.True(snapshot.ReconciliationRequired);
        Assert.Equal(1, snapshot.UnknownCount);
    }

    [Fact]
    public async Task KeyConfirmationCancellationRequiresReconciliation()
    {
        ConfigurableRadioConfirmation confirmation = new()
        {
            CancelKey = true
        };
        Fixture fixture = new(radioConfirmation: confirmation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Subject.SubmitAsync(fixture.Request(enabled: true)));

        StationTxCommandTransactionCompositionDiagnostics snapshot =
            fixture.Subject.Snapshot;
        Assert.Equal(1, confirmation.KeyCalls);
        Assert.Equal(1, fixture.Command.SubmitCalls);
        Assert.True(snapshot.Active);
        Assert.True(snapshot.ReconciliationRequired);
        Assert.Equal(1, snapshot.UnknownCount);
        Assert.Equal("key-confirmation-cancelled", snapshot.LastOutcome);
    }

    [Fact]
    public async Task UnkeyConfirmationCancellationRequiresReconciliation()
    {
        ConfigurableRadioConfirmation confirmation = new()
        {
            CancelUnkey = true
        };
        Fixture fixture = new(radioConfirmation: confirmation);
        await fixture.ArmKeyAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Subject.SubmitAsync(
                fixture.Request(enabled: false, sequence: 2)));

        StationTxCommandTransactionCompositionDiagnostics snapshot =
            fixture.Subject.Snapshot;
        Assert.Equal(1, confirmation.KeyCalls);
        Assert.Equal(1, confirmation.UnkeyCalls);
        Assert.Equal(2, fixture.Command.SubmitCalls);
        Assert.True(snapshot.Active);
        Assert.True(snapshot.ReconciliationRequired);
        Assert.Equal(1, snapshot.UnknownCount);
        Assert.Equal("unkey-confirmation-cancelled", snapshot.LastOutcome);
    }

    private sealed class Fixture
    {
        private const string ConnectionId = "connection-a";
        private static readonly DateTimeOffset Now =
            new(2026, 8, 1, 18, 0, 0, TimeSpan.Zero);

        public Fixture(
            bool safetyAttached = true,
            bool commandAttached = true,
            IStationTxCommandRadioConfirmationParticipant? radioConfirmation = null)
        {
            Time = new ManualTimeProvider(Now);
            Authority = CreateAuthority(armed: false);
            Safety = new FakeSafetyParticipant(this);
            Command = new FakeCommandParticipant(this);
            Subject = new StationTxCommandTransactionComposition(
                safetyAttached ? Safety : null,
                commandAttached ? Command : null,
                ResolveAuthority,
                Time,
                radioConfirmation);
        }

        public ManualTimeProvider Time { get; }
        public StationTxCommandAuthority Authority { get; set; }
        public StationTxCommandAuthorityResolution? AuthorityFailure { get; set; }
        public FakeSafetyParticipant Safety { get; }
        public FakeCommandParticipant Command { get; }
        public StationTxCommandTransactionComposition Subject { get; }

        public StationTxCommandTransactionRequest Request(
            bool enabled,
            long sequence = 1) =>
            new(
                ConnectionId,
                sequence,
                new BrowserTxIntent(
                    $"intent-{sequence}",
                    BrowserTxIntentKind.Mox,
                    "mox.set",
                    enabled,
                    null),
                Time.GetUtcNow(),
                TimeSpan.FromSeconds(1));

        public StationTxCommandTransactionHeartbeatRequest HeartbeatRequest() =>
            new(ConnectionId, TimeSpan.FromSeconds(1));

        public StationTxCommandTransactionAbortRequest AbortRequest() =>
            new(ConnectionId, "operator-abort");

        public async Task ArmKeyAsync()
        {
            StationTxCommandTransactionResult result =
                await Subject.SubmitAsync(Request(enabled: true));
            Assert.True(result.Success, result.Message);
        }

        public StationTxSafetyArmCompositionResult SafetyResult(
            bool success,
            string code) =>
            new(
                success,
                code,
                code,
                Safety.Snapshot,
                SafetyResult: null);

        public StationTxCommandSessionCompositionResult CommandResult(
            bool success,
            string code) =>
            new(
                success,
                code,
                code,
                Command.Snapshot,
                CoordinatorResult: null);

        private StationTxCommandAuthorityResolution ResolveAuthority(
            string? connectionClientId)
        {
            if (AuthorityFailure is not null)
            {
                return AuthorityFailure;
            }
            if (connectionClientId is not null &&
                !string.Equals(
                    connectionClientId.Trim(),
                    ConnectionId,
                    StringComparison.Ordinal))
            {
                return StationTxCommandAuthorityResolution.Rejected(
                    "connection-mismatch",
                    "mismatch");
            }
            return StationTxCommandAuthorityResolution.Accepted(Authority);
        }

        public void SetArmed(bool armed)
        {
            Authority = Authority with
            {
                Safety = CreateSafety(armed)
            };
        }

        private StationTxCommandAuthority CreateAuthority(bool armed) =>
            new(
                StationId: "station-a",
                RadioId: "RADIO-A",
                SessionId: "session-a",
                BrowserClientId: "browser-a",
                LeaseId: "lease-a",
                LeaseExpiresAt: Now.AddMinutes(1),
                GatewayInstanceId: "gateway-a",
                EngineInstanceId: "engine-a",
                ClientHandle: 0x1234abcdu,
                Authenticated: true,
                BrowserFresh: true,
                EngineFresh: true,
                GatewayFresh: true,
                AuthorityFresh: true,
                Occupancy: new RadioTxOccupancySnapshot(
                    "RADIO-A",
                    RadioTxOccupancyState.Idle,
                    Now,
                    Now.AddSeconds(8),
                    [],
                    [new RadioTxOccupant(
                        0x1234abcdu,
                        "AetherSDR-Web",
                        "station-a",
                        "LOCAL",
                        AetherOwned: true)]),
                Safety: CreateSafety(armed));

        private StationTxSafetySnapshot CreateSafety(bool armed) =>
            new(
                RadioId: "RADIO-A",
                State: armed
                    ? StationTxSafetyState.Armed
                    : StationTxSafetyState.Disarmed,
                Reason: armed ? "armed" : "disarmed",
                EngineInstanceId: armed ? Authority?.EngineInstanceId ?? "engine-a" : null,
                LeaseId: armed ? "lease-a" : null,
                SessionId: armed ? "session-a" : null,
                BrowserClientId: armed ? "browser-a" : null,
                ProtectedClientHandle: armed ? 0x1234abcdu : 0,
                ArmedAt: armed ? Now : null,
                LastHeartbeatAt: armed ? Now : null,
                HeartbeatDeadlineAt: armed ? Now.AddSeconds(1) : null,
                UnkeyDeadlineAt: null,
                UnkeyAttempts: 0,
                SawProtectedTransmit: false);
    }

    private sealed class FakeSafetyParticipant(Fixture fixture) :
        IStationTxSafetyArmTransactionParticipant
    {
        public int ArmCalls { get; private set; }
        public int HeartbeatCalls { get; private set; }
        public int AbortCalls { get; private set; }
        public bool? ArmAvailableOverride { get; set; }
        public string? ReasonOverride { get; set; }
        public Exception? ArmException { get; set; }
        public Exception? HeartbeatException { get; set; }
        public Exception? AbortException { get; set; }
        public Action? AfterArm { get; set; }
        public StationTxSafetyArmCompositionResult? ArmResult { get; set; }
        public StationTxSafetyArmCompositionResult? HeartbeatResult { get; set; }
        public StationTxSafetyArmCompositionResult? AbortResult { get; set; }

        public StationTxSafetyArmCompositionDiagnostics Snapshot
        {
            get
            {
                bool armed = fixture.Authority.Safety.State ==
                    StationTxSafetyState.Armed;
                bool armAvailable = ArmAvailableOverride ?? !armed;
                return new StationTxSafetyArmCompositionDiagnostics(
                    Registered: true,
                    ArmAuthorityAttached: true,
                    ArmAuthorityRegistered: true,
                    ArmAuthorityArmAvailable: armAvailable,
                    ArmAuthorityHeartbeatAvailable: armed,
                    ArmAuthorityAbortAvailable: armed,
                    SessionAuthoritySnapshotAvailable: true,
                    ArmAvailable: armAvailable,
                    HeartbeatAvailable: armed,
                    AbortAvailable: armed,
                    AttemptCount: ArmCalls + HeartbeatCalls + AbortCalls,
                    ForwardedCount: ArmCalls + HeartbeatCalls + AbortCalls,
                    AcceptedCount: 0,
                    RejectedCount: 0,
                    LastOperation: "none",
                    LastOutcome: "none",
                    LastObservedAt: null,
                    Reason: ReasonOverride ?? (armed ? "ready" : "ready"));
            }
        }

        public Task<StationTxSafetyArmCompositionResult> ArmAsync(
            StationTxSafetyArmCompositionArmRequest request,
            CancellationToken cancellationToken = default)
        {
            ArmCalls++;
            if (ArmException is not null)
            {
                return Task.FromException<StationTxSafetyArmCompositionResult>(
                    ArmException);
            }
            StationTxSafetyArmCompositionResult result = ArmResult ??
                fixture.SafetyResult(success: true, "armed");
            if (result.Success)
            {
                fixture.SetArmed(true);
                AfterArm?.Invoke();
            }
            return Task.FromResult(result);
        }

        public Task<StationTxSafetyArmCompositionResult> HeartbeatAsync(
            StationTxSafetyArmCompositionHeartbeatRequest request,
            CancellationToken cancellationToken = default)
        {
            HeartbeatCalls++;
            if (HeartbeatException is not null)
            {
                return Task.FromException<StationTxSafetyArmCompositionResult>(
                    HeartbeatException);
            }
            return Task.FromResult(
                HeartbeatResult ?? fixture.SafetyResult(true, "heartbeat"));
        }

        public Task<StationTxSafetyArmCompositionResult> AbortAsync(
            StationTxSafetyArmCompositionAbortRequest request,
            CancellationToken cancellationToken = default)
        {
            AbortCalls++;
            if (AbortException is not null)
            {
                return Task.FromException<StationTxSafetyArmCompositionResult>(
                    AbortException);
            }
            StationTxSafetyArmCompositionResult result = AbortResult ??
                fixture.SafetyResult(true, "aborted");
            if (result.Success)
            {
                fixture.SetArmed(false);
            }
            return Task.FromResult(result);
        }
    }

    private sealed class FakeCommandParticipant(Fixture fixture) :
        IStationTxCommandTransactionSubmissionParticipant
    {
        public bool SubmissionEnabled { get; set; } = true;
        public bool AvailableAfterArm { get; set; } = true;
        public int SubmitCalls { get; private set; }
        public Exception? SubmitException { get; set; }
        public StationTxCommandSessionCompositionResult? KeyResult { get; set; }
        public StationTxCommandSessionCompositionResult? UnkeyResult { get; set; }
        public List<StationTxCommandSessionCompositionRequest> Requests { get; } = [];

        public StationTxCommandSessionCompositionDiagnostics Snapshot
        {
            get
            {
                bool armed = fixture.Authority.Safety.State ==
                    StationTxSafetyState.Armed;
                bool available = armed && AvailableAfterArm && SubmissionEnabled;
                string reason = !SubmissionEnabled
                    ? "submission-disabled"
                    : !armed || !AvailableAfterArm
                        ? "arming-unavailable"
                        : "ready";
                return new StationTxCommandSessionCompositionDiagnostics(
                    Registered: true,
                    CoordinatorAttached: true,
                    BoundaryAttached: true,
                    SubmissionEnabled: SubmissionEnabled,
                    SigningAvailable: true,
                    SignatureVerificationAvailable: true,
                    BoundaryEnabled: true,
                    BoundarySignatureVerificationAvailable: true,
                    CommandAdapterRegistered: true,
                    ArmingAvailable: available,
                    SetTransmitAvailable: available,
                    AuthoritySnapshotAvailable: true,
                    SubmissionAvailable: available,
                    AttemptCount: SubmitCalls,
                    ForwardedCount: SubmitCalls,
                    AcceptedCount: 0,
                    RejectedCount: 0,
                    LastOutcome: "none",
                    LastObservedAt: null,
                    Reason: reason);
            }
        }

        public Task<StationTxCommandSessionCompositionResult> SubmitAsync(
            StationTxCommandSessionCompositionRequest request,
            CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            Requests.Add(request);
            if (SubmitException is not null)
            {
                return Task.FromException<StationTxCommandSessionCompositionResult>(
                    SubmitException);
            }
            return Task.FromResult(
                request.Intent.Enabled == true
                    ? KeyResult ?? fixture.CommandResult(true, "accepted")
                    : UnkeyResult ?? fixture.CommandResult(true, "accepted"));
        }
    }

    private sealed class ConfigurableRadioConfirmation :
        IStationTxCommandRadioConfirmationParticipant
    {
        public bool CancelKey { get; init; }
        public bool CancelUnkey { get; init; }
        public int KeyCalls { get; private set; }
        public int UnkeyCalls { get; private set; }

        public Task<StationTxCommandRadioConfirmationResult> ConfirmAsync(
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            if (enabled)
            {
                KeyCalls++;
                if (CancelKey)
                {
                    return Task.FromException<
                        StationTxCommandRadioConfirmationResult>(
                            new OperationCanceledException());
                }
            }
            else
            {
                UnkeyCalls++;
                if (CancelUnkey)
                {
                    return Task.FromException<
                        StationTxCommandRadioConfirmationResult>(
                            new OperationCanceledException());
                }
            }

            return Task.FromResult(
                new StationTxCommandRadioConfirmationResult(
                    Success: true,
                    OutcomeKnown: true,
                    Code: enabled ? "key_confirmed" : "unkey_confirmed",
                    Message: "confirmed"));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
