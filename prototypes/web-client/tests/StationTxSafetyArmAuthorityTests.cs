using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxSafetyArmAuthorityTests
{
    private const string StationId = "gateway-a";
    private const string RadioId = "FLEX:RADIO-A";
    private const string SessionId = "session-a";
    private const string BrowserClientId = "browser-a";
    private const string LeaseId = "lease-a";
    private const string EngineInstanceId = "engine-a";
    private const uint ClientHandle = 0x1234abcd;
    private const uint ObserverHandle = 0x55667788;
    private const uint ExternalHandle = 0x8899aabb;

    [Fact]
    public void AuthorityIsPrivateAndHasNoExternalIngressContract()
    {
        Assert.False(typeof(StationTxSafetyArmAuthority).IsPublic);
        Assert.Contains(
            typeof(IStationTxSafetyArmAuthority),
            typeof(StationTxSafetyArmAuthority).GetInterfaces());
        Assert.DoesNotContain(
            typeof(StationTxSafetyArmAuthority).GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly),
            method => method.Name.Contains("Browser", StringComparison.Ordinal) ||
                method.Name.Contains("WebSocket", StringComparison.Ordinal) ||
                method.Name.Contains("Http", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadyPreArmStateExposesOnlyArmCapability()
    {
        Fixture fixture = new();

        StationTxSafetyArmAuthorityDiagnostics snapshot =
            fixture.Subject.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.BoundaryRegistered);
        Assert.True(snapshot.BoundaryEnabled);
        Assert.True(snapshot.SignatureVerificationAvailable);
        Assert.True(snapshot.CommandAdapterRegistered);
        Assert.True(snapshot.AdapterExecutorAttached);
        Assert.True(snapshot.AdapterExecutorRegistered);
        Assert.True(snapshot.GateExecutorRegistered);
        Assert.True(snapshot.GateTransmitEnabled);
        Assert.True(snapshot.CommandTransportAvailable);
        Assert.True(snapshot.GateSetTransmitAvailable);
        Assert.True(snapshot.SessionAuthoritySnapshotAvailable);
        Assert.Equal("Idle", snapshot.GateState);
        Assert.Equal("Disarmed", snapshot.SafetyState);
        Assert.True(snapshot.ArmAvailable);
        Assert.False(snapshot.HeartbeatAvailable);
        Assert.False(snapshot.AbortAvailable);
        Assert.Equal("ready", snapshot.Reason);
        Assert.Equal(0, snapshot.AttemptCount);
    }

    [Fact]
    public void DisabledProductionPathRegistersButAuthorizesNothing()
    {
        Fixture fixture = new();
        fixture.DisableCommandPath();

        StationTxSafetyArmAuthorityDiagnostics snapshot =
            fixture.Subject.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.BoundaryRegistered);
        Assert.False(snapshot.BoundaryEnabled);
        Assert.True(snapshot.CommandAdapterRegistered);
        Assert.True(snapshot.AdapterExecutorAttached);
        Assert.True(snapshot.AdapterExecutorRegistered);
        Assert.True(snapshot.GateExecutorRegistered);
        Assert.False(snapshot.GateTransmitEnabled);
        Assert.False(snapshot.CommandTransportAvailable);
        Assert.False(snapshot.GateSetTransmitAvailable);
        Assert.False(snapshot.ArmAvailable);
        Assert.False(snapshot.HeartbeatAvailable);
        Assert.False(snapshot.AbortAvailable);
        Assert.Equal("boundary-disabled", snapshot.Reason);
    }

    [Fact]
    public async Task ExactArmAuthorizationIsAcceptedOnce()
    {
        Fixture fixture = new();
        StationTxSafetyArmAuthorizationRequest request = fixture.ArmRequest();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(request);

        Assert.True(result.Success);
        Assert.Equal("authorized", result.Code);
        Assert.Equal(1, fixture.ResolverCalls);
        StationTxSafetyArmAuthorityDiagnostics snapshot =
            fixture.Subject.Snapshot;
        Assert.Equal(1, snapshot.AttemptCount);
        Assert.Equal(1, snapshot.AcceptedCount);
        Assert.Equal(0, snapshot.RejectedCount);
        Assert.Equal("arm", snapshot.LastOperation);
        Assert.Equal("authorized", snapshot.LastOutcome);
        Assert.Equal(2, fixture.ResolverCalls);
    }

    [Fact]
    public async Task DisabledBoundaryRejectsBeforeAuthorization()
    {
        Fixture fixture = new();
        fixture.Boundary = fixture.Boundary with { BoundaryEnabled = false };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("boundary_disabled", result.Code);
    }

    [Fact]
    public async Task MissingSignatureVerifierRejectsArm()
    {
        Fixture fixture = new();
        fixture.Boundary = fixture.Boundary with
        {
            SignatureVerificationAvailable = false
        };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("signature_verifier_unavailable", result.Code);
    }

    [Fact]
    public async Task MissingAdapterRejectsArm()
    {
        Fixture fixture = new();
        fixture.Adapter = fixture.Adapter with
        {
            CommandAdapterRegistered = false
        };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("adapter_unavailable", result.Code);
    }

    [Fact]
    public async Task MissingGateExecutorRejectsArm()
    {
        Fixture fixture = new();
        fixture.Executor = fixture.Executor with { Registered = false };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("gate_executor_unavailable", result.Code);
    }

    [Fact]
    public async Task DisabledTransmitRejectsArm()
    {
        Fixture fixture = new();
        fixture.GateCapabilities = fixture.GateCapabilities with
        {
            TransmitEnabled = false,
            SetTransmitAvailable = false
        };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("transmit_disabled", result.Code);
    }

    [Fact]
    public async Task MissingCommandTransportRejectsArm()
    {
        Fixture fixture = new();
        fixture.GateCapabilities = fixture.GateCapabilities with
        {
            CommandTransportAvailable = false,
            SetTransmitAvailable = false
        };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("command_transport_unavailable", result.Code);
    }

    [Fact]
    public async Task NonIdleGateRejectsArm()
    {
        Fixture fixture = new();
        fixture.Gate = fixture.Gate with
        {
            State = StationTxGateState.Faulted,
            Reason = "faulted"
        };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("gate_not_idle", result.Code);
    }

    [Fact]
    public async Task NonDisarmedSupervisorRejectsArm()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: true, gateHasIntent: false);

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("safety_not_disarmed", result.Code);
    }

    [Fact]
    public async Task NonIdleOccupancyRejectsArm()
    {
        Fixture fixture = new();
        fixture.SetExactTransmit(gateHasIntent: false);
        fixture.SetDisarmed();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("radio_not_idle", result.Code);
    }

    [Fact]
    public async Task WrongLocalPttOwnerRejectsArm()
    {
        Fixture fixture = new();
        fixture.Occupancy = fixture.IdleOccupancy(ExternalHandle);
        fixture.RefreshAuthority();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("local_ptt_authority_mismatch", result.Code);
    }

    [Fact]
    public async Task ReplacedTupleRejectsArm()
    {
        Fixture fixture = new();
        StationTxCommandAuthority stale = fixture.CurrentAuthority;
        fixture.LeaseExpiresAt = fixture.LeaseExpiresAt.AddSeconds(1);
        fixture.RefreshAuthority();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(
                fixture.ArmRequest(stale));

        Assert.False(result.Success);
        Assert.Equal("authority_tuple_mismatch", result.Code);
    }

    [Fact]
    public async Task ExpiredLeaseRejectsArm()
    {
        Fixture fixture = new();
        fixture.LeaseExpiresAt = fixture.Time.GetUtcNow();
        fixture.RefreshAuthority();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("lease_expired", result.Code);
    }

    [Fact]
    public async Task StaleLifecycleAuthorityRejectsArm()
    {
        Fixture fixture = new();
        fixture.AuthorityFresh = false;
        fixture.RefreshAuthority();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("authority_stale", result.Code);
    }

    [Fact]
    public async Task ResolverRejectionIsPreservedAndNormalized()
    {
        Fixture fixture = new();
        StationTxCommandAuthority supplied = fixture.CurrentAuthority;
        fixture.Resolution = StationTxCommandAuthorityResolution.Rejected(
            "lease-unavailable",
            "No active lease is available.");

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest(supplied));

        Assert.False(result.Success);
        Assert.Equal("lease_unavailable", result.Code);
        Assert.Equal("No active lease is available.", result.Message);
    }

    [Fact]
    public async Task DependencyExceptionFailsClosed()
    {
        Fixture fixture = new() { ThrowFromGateProvider = true };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("authority_state_unavailable", result.Code);
        Assert.Equal(
            "authority-state-unavailable",
            fixture.Subject.Snapshot.Reason);
    }

    [Fact]
    public async Task InvalidHeartbeatTimeoutStopsBeforeStateRead()
    {
        Fixture fixture = new();
        StationTxSafetyArmAuthorizationRequest request =
            fixture.ArmRequest() with
            {
                HeartbeatTimeout = TimeSpan.FromSeconds(30)
            };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(request);

        Assert.False(result.Success);
        Assert.Equal("invalid_heartbeat_timeout", result.Code);
        Assert.Equal(0, fixture.ResolverCalls);
    }

    [Fact]
    public async Task InvalidAbortReasonStopsBeforeStateRead()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: true, gateHasIntent: false);
        StationTxSafetyArmAuthorizationRequest request =
            fixture.AbortRequest() with { AbortReason = "bad\nreason" };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(request);

        Assert.False(result.Success);
        Assert.Equal("invalid_abort_reason", result.Code);
        Assert.Equal(0, fixture.ResolverCalls);
    }

    [Fact]
    public async Task CancellationBeforeAttemptLeavesDiagnosticsUntouched()
    {
        Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Subject.AuthorizeAsync(
                fixture.ArmRequest(),
                cancellation.Token));

        Assert.Equal(0, fixture.Subject.Snapshot.AttemptCount);
    }

    [Fact]
    public async Task ExactArmedIdleHeartbeatIsAccepted()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: true, gateHasIntent: false);

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.HeartbeatRequest());

        Assert.True(result.Success);
        Assert.Equal("authorized", result.Code);
    }

    [Fact]
    public async Task ExactProtectedTransmitHeartbeatIsAccepted()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: false, gateHasIntent: true);

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.HeartbeatRequest());

        Assert.True(result.Success);
        Assert.Equal("authorized", result.Code);
    }

    [Fact]
    public async Task ExpiredHeartbeatIsRejected()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: true, gateHasIntent: false);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        fixture.RefreshAuthority();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.HeartbeatRequest());

        Assert.False(result.Success);
        Assert.Equal("safety_heartbeat_expired", result.Code);
    }

    [Fact]
    public async Task HeartbeatCannotExtendAfterCommandPathLoss()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: true, gateHasIntent: false);
        fixture.Boundary = fixture.Boundary with { BoundaryEnabled = false };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.HeartbeatRequest());

        Assert.False(result.Success);
        Assert.Equal("boundary_disabled", result.Code);
    }

    [Fact]
    public async Task MismatchedSafetyIdentityRejectsHeartbeat()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: true, gateHasIntent: false);
        fixture.Safety = fixture.Safety with { LeaseId = "lease-b" };
        fixture.RefreshAuthority();

        StationTxCommandAuthority supplied = fixture.CurrentAuthority with
        {
            LeaseId = LeaseId,
            Safety = fixture.CurrentAuthority.Safety with { LeaseId = LeaseId }
        };
        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(
                fixture.HeartbeatRequest(supplied));

        Assert.False(result.Success);
        Assert.Equal("authority_tuple_mismatch", result.Code);
    }

    [Fact]
    public async Task IdleHeartbeatRejectsMovedLocalPttAuthority()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: true, gateHasIntent: false);
        fixture.Occupancy = fixture.IdleOccupancy(ExternalHandle);
        fixture.RefreshAuthority();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.HeartbeatRequest());

        Assert.False(result.Success);
        Assert.Equal("local_ptt_authority_mismatch", result.Code);
    }

    [Fact]
    public async Task ActiveGateIdentityMismatchRejectsHeartbeat()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: false, gateHasIntent: true);
        fixture.Gate = fixture.Gate with { LeaseId = "lease-b" };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.HeartbeatRequest());

        Assert.False(result.Success);
        Assert.Equal("gate_identity_mismatch", result.Code);
    }

    [Fact]
    public async Task AbortRemainsAvailableAfterNormalCommandPathLoss()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: true, gateHasIntent: false);
        fixture.DisableCommandPath();
        fixture.Gate = fixture.Gate with
        {
            State = StationTxGateState.Disabled,
            Reason = "transmit-disabled"
        };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.AbortRequest());

        Assert.True(result.Success);
        StationTxSafetyArmAuthorityDiagnostics snapshot =
            fixture.Subject.Snapshot;
        Assert.False(snapshot.ArmAvailable);
        Assert.False(snapshot.HeartbeatAvailable);
        Assert.True(snapshot.AbortAvailable);
    }

    [Fact]
    public async Task AbortAcceptsExactProtectedTransmitAndGateIdentity()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: false, gateHasIntent: true);

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.AbortRequest());

        Assert.True(result.Success);
    }

    [Fact]
    public async Task AbortRejectsExternalTransmitOwner()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: false, gateHasIntent: true);
        fixture.Occupancy = fixture.ExternalOccupancy();
        fixture.RefreshAuthority();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.AbortRequest());

        Assert.False(result.Success);
        Assert.Equal("tx_ownership_mismatch", result.Code);
    }

    [Fact]
    public async Task AbortRejectsMissingExactArm()
    {
        Fixture fixture = new();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.AbortRequest());

        Assert.False(result.Success);
        Assert.Equal("safety_arm_mismatch", result.Code);
    }

    [Fact]
    public async Task AbortRejectsMismatchedActiveGateIntent()
    {
        Fixture fixture = new();
        fixture.SetArmed(idle: false, gateHasIntent: true);
        fixture.Gate = fixture.Gate with { ClientHandle = ExternalHandle };

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.AbortRequest());

        Assert.False(result.Success);
        Assert.Equal("gate_identity_mismatch", result.Code);
    }

    [Fact]
    public async Task OneRequestPerformsOneStateReadAndNoRetry()
    {
        Fixture fixture = new();

        StationTxSafetyArmAuthorizationResult result =
            await fixture.Subject.AuthorizeAsync(fixture.ArmRequest());

        Assert.True(result.Success);
        Assert.Equal(1, fixture.BoundaryReads);
        Assert.Equal(1, fixture.AdapterReads);
        Assert.Equal(1, fixture.ExecutorReads);
        Assert.Equal(1, fixture.GateCapabilityReads);
        Assert.Equal(1, fixture.GateSnapshotReads);
        Assert.Equal(1, fixture.SafetyReads);
        Assert.Equal(1, fixture.ResolverCalls);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Time = new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.Zero));
            LeaseExpiresAt = Time.GetUtcNow().AddMinutes(1);
            Occupancy = IdleOccupancy(ClientHandle);
            Safety = DisarmedSafety();
            Boundary = ReadyBoundary(armed: false);
            Adapter = ReadyAdapter(armed: false);
            Executor = new StationTxCommandAdapterExecutorCapabilities(
                Registered: true,
                ArmingAvailable: true,
                SetTransmitAvailable: true,
                Reason: "ready");
            GateCapabilities = new StationTxCommandGateCapabilities(
                Registered: true,
                TransmitEnabled: true,
                CommandTransportAvailable: true,
                SetTransmitAvailable: true,
                Reason: "ready");
            Gate = IdleGate();
            RefreshAuthority();
            Subject = new StationTxSafetyArmAuthority(
                () =>
                {
                    BoundaryReads++;
                    return Boundary;
                },
                () =>
                {
                    AdapterReads++;
                    return Adapter;
                },
                () =>
                {
                    ExecutorReads++;
                    return Executor;
                },
                () =>
                {
                    GateCapabilityReads++;
                    if (ThrowFromGateProvider)
                    {
                        throw new InvalidOperationException("gate failure");
                    }
                    return GateCapabilities;
                },
                () =>
                {
                    GateSnapshotReads++;
                    return Gate;
                },
                () =>
                {
                    SafetyReads++;
                    return Safety;
                },
                _ =>
                {
                    ResolverCalls++;
                    return Resolution;
                },
                Time);
        }

        public ManualTimeProvider Time { get; }
        public StationTxSafetyArmAuthority Subject { get; }
        public StationTxCommandCapabilities Boundary { get; set; }
        public StationTxCommandAdapterCompositionDiagnostics Adapter { get; set; }
        public StationTxCommandAdapterExecutorCapabilities Executor { get; set; }
        public StationTxCommandGateCapabilities GateCapabilities { get; set; }
        public StationTxGateSnapshot Gate { get; set; }
        public StationTxSafetySnapshot Safety { get; set; }
        public RadioTxOccupancySnapshot Occupancy { get; set; }
        public StationTxCommandAuthorityResolution Resolution { get; set; } =
            StationTxCommandAuthorityResolution.Rejected(
                "not-ready",
                "not ready");
        public DateTimeOffset LeaseExpiresAt { get; set; }
        public bool AuthorityFresh { get; set; } = true;
        public bool ThrowFromGateProvider { get; set; }
        public int BoundaryReads { get; private set; }
        public int AdapterReads { get; private set; }
        public int ExecutorReads { get; private set; }
        public int GateCapabilityReads { get; private set; }
        public int GateSnapshotReads { get; private set; }
        public int SafetyReads { get; private set; }
        public int ResolverCalls { get; private set; }

        public StationTxCommandAuthority CurrentAuthority =>
            Resolution.Authority!;

        public void RefreshAuthority()
        {
            Resolution = StationTxCommandAuthorityResolution.Accepted(
                new StationTxCommandAuthority(
                    StationId,
                    RadioId,
                    SessionId,
                    BrowserClientId,
                    LeaseId,
                    LeaseExpiresAt,
                    StationId,
                    EngineInstanceId,
                    ClientHandle,
                    Authenticated: true,
                    BrowserFresh: true,
                    EngineFresh: true,
                    GatewayFresh: true,
                    AuthorityFresh,
                    Occupancy,
                    Safety));
        }

        public void SetDisarmed()
        {
            Safety = DisarmedSafety();
            Boundary = ReadyBoundary(armed: false);
            Adapter = ReadyAdapter(armed: false);
            RefreshAuthority();
        }

        public void SetArmed(bool idle, bool gateHasIntent)
        {
            Safety = ArmedSafety(Time.GetUtcNow());
            Occupancy = idle
                ? IdleOccupancy(ClientHandle)
                : ExactTransmitOccupancy();
            Boundary = ReadyBoundary(armed: true);
            Adapter = ReadyAdapter(armed: true);
            Gate = gateHasIntent ? KeyedGate() : IdleGate();
            RefreshAuthority();
        }

        public void SetExactTransmit(bool gateHasIntent)
        {
            Occupancy = ExactTransmitOccupancy();
            Gate = gateHasIntent ? KeyedGate() : IdleGate();
            RefreshAuthority();
        }

        public void DisableCommandPath()
        {
            Boundary = Boundary with
            {
                BoundaryEnabled = false,
                ArmingAvailable = false,
                SetTransmitAvailable = false,
                Reason = "boundary-disabled"
            };
            Adapter = Adapter with
            {
                ExecutorArmingAvailable = false,
                ExecutorSetTransmitAvailable = false,
                ArmingAvailable = false,
                SetTransmitAvailable = false,
                Reason = "executor-arming-unavailable"
            };
            Executor = Executor with
            {
                ArmingAvailable = false,
                SetTransmitAvailable = false,
                Reason = "transmit-disabled"
            };
            GateCapabilities = GateCapabilities with
            {
                TransmitEnabled = false,
                CommandTransportAvailable = false,
                SetTransmitAvailable = false,
                Reason = "transmit-disabled"
            };
        }

        public StationTxSafetyArmAuthorizationRequest ArmRequest(
            StationTxCommandAuthority? authority = null) =>
            new(
                StationTxSafetyArmOperation.Arm,
                authority ?? CurrentAuthority,
                TimeSpan.FromSeconds(2),
                AbortReason: null);

        public StationTxSafetyArmAuthorizationRequest HeartbeatRequest(
            StationTxCommandAuthority? authority = null) =>
            new(
                StationTxSafetyArmOperation.Heartbeat,
                authority ?? CurrentAuthority,
                TimeSpan.FromSeconds(2),
                AbortReason: null);

        public StationTxSafetyArmAuthorizationRequest AbortRequest(
            StationTxCommandAuthority? authority = null) =>
            new(
                StationTxSafetyArmOperation.Abort,
                authority ?? CurrentAuthority,
                HeartbeatTimeout: null,
                AbortReason: "operator-abort");

        public RadioTxOccupancySnapshot IdleOccupancy(uint localPttHandle) =>
            new(
                RadioId,
                RadioTxOccupancyState.Idle,
                Time.GetUtcNow(),
                Time.GetUtcNow().AddSeconds(8),
                Occupants: [],
                LocalPttOwners:
                [
                    Occupant(localPttHandle, localPttHandle == ClientHandle)
                ]);

        public RadioTxOccupancySnapshot ExternalOccupancy() =>
            new(
                RadioId,
                RadioTxOccupancyState.External,
                Time.GetUtcNow(),
                Time.GetUtcNow().AddSeconds(8),
                Occupants: [Occupant(ExternalHandle, aetherOwned: false)],
                LocalPttOwners: [Occupant(ClientHandle, aetherOwned: true)]);

        private RadioTxOccupancySnapshot ExactTransmitOccupancy() =>
            new(
                RadioId,
                RadioTxOccupancyState.AetherOwned,
                Time.GetUtcNow(),
                Time.GetUtcNow().AddSeconds(8),
                Occupants: [Occupant(ClientHandle, aetherOwned: true)],
                LocalPttOwners: [Occupant(ClientHandle, aetherOwned: true)]);

        private StationTxGateSnapshot IdleGate() =>
            new(
                RadioId,
                StationTxGateState.Idle,
                "idle",
                LeaseId: null,
                SessionId: null,
                BrowserClientId: null,
                ClientHandle: 0,
                IntentCreatedAt: null,
                DeadlineAt: null,
                UnkeyAttempts: 0);

        private StationTxGateSnapshot KeyedGate() =>
            new(
                RadioId,
                StationTxGateState.Keyed,
                "radio-confirmed-keyed",
                LeaseId,
                SessionId,
                BrowserClientId,
                ClientHandle,
                Time.GetUtcNow(),
                DeadlineAt: null,
                UnkeyAttempts: 0);

        private StationTxSafetySnapshot DisarmedSafety() =>
            new(
                RadioId,
                StationTxSafetyState.Disarmed,
                "disarmed",
                EngineInstanceId: null,
                LeaseId: null,
                SessionId: null,
                BrowserClientId: null,
                ProtectedClientHandle: 0,
                ArmedAt: null,
                LastHeartbeatAt: null,
                HeartbeatDeadlineAt: null,
                UnkeyDeadlineAt: null,
                UnkeyAttempts: 0,
                SawProtectedTransmit: false);

        private StationTxSafetySnapshot ArmedSafety(DateTimeOffset now) =>
            new(
                RadioId,
                StationTxSafetyState.Armed,
                "armed",
                EngineInstanceId,
                LeaseId,
                SessionId,
                BrowserClientId,
                ClientHandle,
                ArmedAt: now,
                LastHeartbeatAt: now,
                HeartbeatDeadlineAt: now.AddSeconds(2),
                UnkeyDeadlineAt: null,
                UnkeyAttempts: 0,
                SawProtectedTransmit: false);

        private static StationTxCommandCapabilities ReadyBoundary(bool armed) =>
            new(
                StationTxCommandBoundary.ProtocolVersion,
                BoundaryRegistered: true,
                BoundaryEnabled: true,
                SignatureVerificationAvailable: true,
                CommandAdapterRegistered: true,
                ArmingAvailable: armed,
                SetTransmitAvailable: armed,
                Reason: armed ? "available" : "safety-not-armed");

        private static StationTxCommandAdapterCompositionDiagnostics ReadyAdapter(
            bool armed) =>
            new(
                Registered: true,
                ExecutorAttached: true,
                ExecutorRegistered: true,
                ExecutorArmingAvailable: true,
                ExecutorSetTransmitAvailable: true,
                AuthoritySnapshotAvailable: true,
                CommandAdapterRegistered: true,
                ArmingAvailable: armed,
                SetTransmitAvailable: armed,
                AttemptCount: 0,
                ForwardedCount: 0,
                AcceptedCount: 0,
                RejectedCount: 0,
                LastOutcome: "none",
                LastObservedAt: null,
                Reason: armed ? "ready" : "safety-not-armed");

        private static RadioTxOccupant Occupant(
            uint handle,
            bool aetherOwned) =>
            new(
                handle,
                aetherOwned ? "AetherD" : "SmartSDR-Win",
                aetherOwned ? "AETHER-ENGINE" : "EXTERNAL",
                "test",
                aetherOwned);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration) =>
            m_now = m_now.Add(duration);
    }
}
