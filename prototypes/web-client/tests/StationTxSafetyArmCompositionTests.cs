using System.Reflection;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxSafetyArmCompositionTests
{
    private const string RadioId = "FLEX:RADIO-A";
    private const string StationId = "gateway-a";
    private const string SessionId = "session-a";
    private const string BrowserClientId = "browser-a";
    private const string ConnectionClientId = "connection-a";
    private const string LeaseId = "lease-a";
    private const string EngineInstanceId = "engine-a";
    private const uint ProtectedHandle = 0x10203040;
    private const uint ObserverHandle = 0x50607080;
    private const uint ExternalHandle = 0x90A0B0C0;

    [Fact]
    public void CompositionAndAuthoritySurfaceRemainPrivate()
    {
        Assert.False(typeof(StationTxSafetyArmComposition).IsPublic);
        Assert.False(typeof(IStationTxSafetyArmAuthority).IsPublic);

        Assert.DoesNotContain(
            typeof(RadioSessionRegistry)
                .GetConstructors(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType ==
                typeof(IStationTxSafetyArmAuthority));
        Assert.DoesNotContain(
            typeof(RadioCoordinator)
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly),
            method => method.Name.Contains(
                "SafetyArm",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(RadioWebSocketEndpoint)
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Static |
                    BindingFlags.Instance),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(StationTxSafetyArmComposition)));
    }

    [Fact]
    public async Task AbsentArmAuthorityKeepsEveryOperationUnavailable()
    {
        await using Fixture fixture = CreateFixture(armAuthority: null);
        ObserveIdle(fixture, ProtectedHandle);

        StationTxSafetyArmCompositionDiagnostics snapshot =
            fixture.Composition.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.False(snapshot.ArmAuthorityAttached);
        Assert.False(snapshot.ArmAuthorityRegistered);
        Assert.True(snapshot.SessionAuthoritySnapshotAvailable);
        Assert.False(snapshot.ArmAvailable);
        Assert.False(snapshot.HeartbeatAvailable);
        Assert.False(snapshot.AbortAvailable);
        Assert.Equal("arm-authority-unattached", snapshot.Reason);

        StationTxSafetyArmCompositionResult result =
            await fixture.Composition.ArmAsync(ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("arm_authority_unattached", result.Code);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(1, result.Diagnostics.AttemptCount);
        Assert.Equal(0, result.Diagnostics.ForwardedCount);
    }

    [Fact]
    public async Task UnregisteredArmAuthorityFailsClosed()
    {
        RecordingArmAuthority authority = new()
        {
            CapabilitiesValue = new(
                Registered: false,
                ArmAvailable: true,
                HeartbeatAvailable: true,
                AbortAvailable: true,
                Reason: "authority-not-ready")
        };
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);

        StationTxSafetyArmCompositionResult result =
            await fixture.Composition.ArmAsync(ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("arm_authority_unregistered", result.Code);
        Assert.Empty(authority.Requests);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
        Assert.Equal("authority-not-ready", result.Diagnostics.Reason);
    }

    [Theory]
    [InlineData("", "invalid_connection_client_id")]
    [InlineData("other-connection", "connection_mismatch")]
    public async Task InvalidOrReplacedConnectionNeverReachesAuthority(
        string connectionClientId,
        string expectedCode)
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);

        StationTxSafetyArmCompositionResult result =
            await fixture.Composition.ArmAsync(
                new StationTxSafetyArmCompositionArmRequest(
                    connectionClientId,
                    TimeSpan.FromSeconds(2)));

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.Empty(authority.Requests);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
    }

    [Theory]
    [InlineData(249, "invalid_heartbeat_timeout")]
    [InlineData(5001, "invalid_heartbeat_timeout")]
    public async Task HeartbeatTimeoutIsBoundedBeforeAuthorityResolution(
        int milliseconds,
        string expectedCode)
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);

        StationTxSafetyArmCompositionResult result =
            await fixture.Composition.ArmAsync(
                new StationTxSafetyArmCompositionArmRequest(
                    ConnectionClientId,
                    TimeSpan.FromMilliseconds(milliseconds)));

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.Empty(authority.Requests);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
    }

    [Fact]
    public async Task ExactArmUsesOnlyLifecycleResolvedIdentity()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);

        StationTxSafetyArmCompositionResult result =
            await fixture.Composition.ArmAsync(ArmRequest());

        Assert.True(result.Success);
        Assert.Equal("armed", result.Code);
        StationTxSafetyArmAuthorizationRequest request =
            Assert.Single(authority.Requests);
        Assert.Equal(StationTxSafetyArmOperation.Arm, request.Operation);
        Assert.Equal(StationId, request.Authority.StationId);
        Assert.Equal(RadioId, request.Authority.RadioId);
        Assert.Equal(SessionId, request.Authority.SessionId);
        Assert.Equal(BrowserClientId, request.Authority.BrowserClientId);
        Assert.Equal(LeaseId, request.Authority.LeaseId);
        Assert.Equal(EngineInstanceId, request.Authority.EngineInstanceId);
        Assert.Equal(ProtectedHandle, request.Authority.ClientHandle);
        Assert.Equal(TimeSpan.FromSeconds(2), request.HeartbeatTimeout);
        Assert.Null(request.AbortReason);

        StationTxSafetySnapshot safety = fixture.Supervisor.Snapshot;
        Assert.Equal(StationTxSafetyState.Armed, safety.State);
        Assert.Equal(EngineInstanceId, safety.EngineInstanceId);
        Assert.Equal(LeaseId, safety.LeaseId);
        Assert.Equal(SessionId, safety.SessionId);
        Assert.Equal(BrowserClientId, safety.BrowserClientId);
        Assert.Equal(ProtectedHandle, safety.ProtectedClientHandle);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(1, result.Diagnostics.ForwardedCount);
        Assert.Equal(1, result.Diagnostics.AcceptedCount);
        Assert.Equal("arm", result.Diagnostics.LastOperation);
        Assert.Equal("armed", result.Diagnostics.LastOutcome);
    }

    [Theory]
    [InlineData("unauthenticated", "authentication_stale")]
    [InlineData("authority-stale", "authority_stale")]
    [InlineData("lease-expired", "lease_expired")]
    [InlineData("handle-missing", "client_handle_unavailable")]
    [InlineData("radio-mismatch", "radio_mismatch")]
    public async Task ArmRejectsInvalidLifecycleAuthority(
        string condition,
        string expectedCode)
    {
        RecordingArmAuthority armAuthority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(armAuthority);
        ObserveIdle(fixture, ProtectedHandle);
        fixture.AuthorityTransform = authority => condition switch
        {
            "unauthenticated" => authority with { Authenticated = false },
            "authority-stale" => authority with { GatewayFresh = false },
            "lease-expired" => authority with
            {
                LeaseExpiresAt = fixture.Time.GetUtcNow()
            },
            "handle-missing" => authority with { ClientHandle = 0 },
            "radio-mismatch" => authority with { RadioId = "FLEX:OTHER" },
            _ => authority
        };

        StationTxSafetyArmCompositionResult result =
            await fixture.Composition.ArmAsync(ArmRequest());

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.Empty(armAuthority.Requests);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
    }

    [Fact]
    public async Task ArmRequiresFreshIdleOccupancy()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);

        StationTxSafetyArmCompositionResult missing =
            await fixture.Composition.ArmAsync(ArmRequest());
        Assert.False(missing.Success);
        Assert.Equal("occupancy_stale", missing.Code);

        ObserveProtectedTransmit(fixture);
        StationTxSafetyArmCompositionResult transmitting =
            await fixture.Composition.ArmAsync(ArmRequest());
        Assert.False(transmitting.Success);
        Assert.Equal("radio_not_idle", transmitting.Code);

        Assert.Empty(authority.Requests);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
    }

    [Fact]
    public async Task ArmRequiresExclusiveLocalPttForProtectedHandle()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ExternalHandle);

        StationTxSafetyArmCompositionResult result =
            await fixture.Composition.ArmAsync(ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("local_ptt_authority_mismatch", result.Code);
        Assert.Empty(authority.Requests);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
    }

    [Fact]
    public async Task AuthorizationRejectionNeverReachesSupervisor()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        authority.Results.Enqueue(
            StationTxSafetyArmAuthorizationResult.Rejected(
                "policy_denied",
                "The independent authority denied the arm."));
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);

        StationTxSafetyArmCompositionResult result =
            await fixture.Composition.ArmAsync(ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("policy_denied", result.Code);
        Assert.Single(authority.Requests);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
        Assert.Equal(0, result.Diagnostics.ForwardedCount);
    }

    [Fact]
    public async Task ExactHeartbeatRenewsOnlyTheCurrentArm()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);
        Assert.True((await fixture.Composition.ArmAsync(ArmRequest())).Success);
        DateTimeOffset firstDeadline =
            fixture.Supervisor.Snapshot.HeartbeatDeadlineAt!.Value;
        fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
        ObserveIdle(fixture, ProtectedHandle);

        StationTxSafetyArmCompositionResult heartbeat =
            await fixture.Composition.HeartbeatAsync(
                new StationTxSafetyArmCompositionHeartbeatRequest(
                    ConnectionClientId,
                    TimeSpan.FromSeconds(3)));

        Assert.True(heartbeat.Success);
        Assert.Equal("heartbeat", heartbeat.Code);
        Assert.True(
            fixture.Supervisor.Snapshot.HeartbeatDeadlineAt > firstDeadline);
        StationTxSafetyArmAuthorizationRequest request = authority.Requests[1];
        Assert.Equal(StationTxSafetyArmOperation.Heartbeat, request.Operation);
        Assert.Equal(LeaseId, request.Authority.LeaseId);
        Assert.Equal(ProtectedHandle, request.Authority.ClientHandle);
        Assert.Equal(2, heartbeat.Diagnostics.ForwardedCount);
    }

    [Fact]
    public async Task IdleHeartbeatRejectsLostLocalPttAuthority()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);
        Assert.True((await fixture.Composition.ArmAsync(ArmRequest())).Success);
        DateTimeOffset deadline =
            fixture.Supervisor.Snapshot.HeartbeatDeadlineAt!.Value;
        ObserveIdle(fixture, ExternalHandle);

        StationTxSafetyArmCompositionResult heartbeat =
            await fixture.Composition.HeartbeatAsync(HeartbeatRequest());

        Assert.False(heartbeat.Success);
        Assert.Equal("local_ptt_authority_mismatch", heartbeat.Code);
        Assert.Equal(deadline, fixture.Supervisor.Snapshot.HeartbeatDeadlineAt);
        Assert.Single(authority.Requests);
    }

    [Fact]
    public async Task HeartbeatRejectsChangedLeaseWithoutExtendingArm()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);
        Assert.True((await fixture.Composition.ArmAsync(ArmRequest())).Success);
        DateTimeOffset deadline =
            fixture.Supervisor.Snapshot.HeartbeatDeadlineAt!.Value;
        fixture.AuthorityTransform = current => current with { LeaseId = "lease-b" };

        StationTxSafetyArmCompositionResult heartbeat =
            await fixture.Composition.HeartbeatAsync(HeartbeatRequest());

        Assert.False(heartbeat.Success);
        Assert.Equal("safety_arm_mismatch", heartbeat.Code);
        Assert.Equal(deadline, fixture.Supervisor.Snapshot.HeartbeatDeadlineAt);
        Assert.Single(authority.Requests);
    }

    [Fact]
    public async Task HeartbeatRejectsExternalOrAmbiguousTransmitOwnership()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);
        Assert.True((await fixture.Composition.ArmAsync(ArmRequest())).Success);
        ObserveExternalTransmit(fixture);

        StationTxSafetyArmCompositionResult heartbeat =
            await fixture.Composition.HeartbeatAsync(HeartbeatRequest());

        Assert.False(heartbeat.Success);
        Assert.Equal("tx_ownership_mismatch", heartbeat.Code);
        Assert.Single(authority.Requests);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task AbortWhileIdleClearsExactArmWithoutRadioCommand()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);
        Assert.True((await fixture.Composition.ArmAsync(ArmRequest())).Success);

        StationTxSafetyArmCompositionResult abort =
            await fixture.Composition.AbortAsync(
                new StationTxSafetyArmCompositionAbortRequest(
                    ConnectionClientId,
                    "operator-abort"));

        Assert.True(abort.Success);
        Assert.Equal("unkeyed", abort.Code);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
        Assert.Empty(fixture.Transport.Commands);
        StationTxSafetyArmAuthorizationRequest request = authority.Requests[1];
        Assert.Equal(StationTxSafetyArmOperation.Abort, request.Operation);
        Assert.Equal("operator-abort", request.AbortReason);
    }

    [Fact]
    public async Task AbortProtectedTransmitUsesOneOwnershipSafeUnkeyAttempt()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);
        Assert.True((await fixture.Composition.ArmAsync(ArmRequest())).Success);
        ObserveProtectedTransmit(fixture);

        StationTxSafetyArmCompositionResult abort =
            await fixture.Composition.AbortAsync(
                new StationTxSafetyArmCompositionAbortRequest(
                    ConnectionClientId,
                    "operator-abort"));

        Assert.True(abort.Success);
        Assert.Equal("unkey_pending", abort.Code);
        Assert.Equal(StationTxSafetyState.UnkeyPending, fixture.Supervisor.Snapshot.State);
        Assert.Single(fixture.Transport.Commands);
        Assert.Equal(2, abort.Diagnostics.AcceptedCount);
        Assert.Equal(2, abort.Diagnostics.ForwardedCount);
    }

    [Fact]
    public async Task AbortNeverForwardsExternalTransmitOwnership()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);
        Assert.True((await fixture.Composition.ArmAsync(ArmRequest())).Success);
        ObserveExternalTransmit(fixture);

        StationTxSafetyArmCompositionResult abort =
            await fixture.Composition.AbortAsync(
                new StationTxSafetyArmCompositionAbortRequest(
                    ConnectionClientId,
                    "operator-abort"));

        Assert.False(abort.Success);
        Assert.Equal("tx_ownership_mismatch", abort.Code);
        Assert.Single(authority.Requests);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal(StationTxSafetyState.Armed, fixture.Supervisor.Snapshot.State);
    }

    [Fact]
    public async Task CancelledAuthorizationPropagatesWithoutSupervisorMutation()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        authority.CancelAuthorization = true;
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Composition.ArmAsync(ArmRequest()));

        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
        Assert.Empty(fixture.Transport.Commands);
        Assert.Equal("cancelled", fixture.Composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task AuthorizationExceptionPropagatesWithoutRetry()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        authority.ThrowAuthorization = true;
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Composition.ArmAsync(ArmRequest()));

        Assert.Single(authority.Requests);
        Assert.Equal(StationTxSafetyState.Disarmed, fixture.Supervisor.Snapshot.State);
        Assert.Equal(
            "arm-authority-exception",
            fixture.Composition.Snapshot.LastOutcome);
    }

    [Fact]
    public async Task CapabilityFaultFailsClosedBeforeAuthorization()
    {
        RecordingArmAuthority authority = ReadyAuthority();
        authority.ThrowCapabilities = true;
        await using Fixture fixture = CreateFixture(authority);
        ObserveIdle(fixture, ProtectedHandle);

        StationTxSafetyArmCompositionResult result =
            await fixture.Composition.ArmAsync(ArmRequest());

        Assert.False(result.Success);
        Assert.Equal("arm_authority_unregistered", result.Code);
        Assert.Empty(authority.Requests);
        Assert.Equal(
            "arm-authority-capabilities-faulted",
            result.Diagnostics.Reason);
    }

    private static Fixture CreateFixture(
        RecordingArmAuthority? armAuthority = null)
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        RadioTxOccupancyRegistry occupancy = new(time);
        FakeEmergencyTransport transport = new();
        StationTxSafetySupervisor supervisor = new(
            RadioId,
            occupancy,
            transport,
            time);
        return new Fixture(
            time,
            occupancy,
            transport,
            supervisor,
            armAuthority);
    }

    private static RecordingArmAuthority ReadyAuthority() => new()
    {
        CapabilitiesValue = new(
            Registered: true,
            ArmAvailable: true,
            HeartbeatAvailable: true,
            AbortAvailable: true,
            Reason: "ready")
    };

    private static StationTxSafetyArmCompositionArmRequest ArmRequest() =>
        new(ConnectionClientId, TimeSpan.FromSeconds(2));

    private static StationTxSafetyArmCompositionHeartbeatRequest
        HeartbeatRequest() =>
        new(ConnectionClientId, TimeSpan.FromSeconds(2));

    private static void ObserveIdle(Fixture fixture, uint localPttHandle) =>
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "engine-observer",
            ProtectedHandle,
            "READY",
            null,
            null,
            Clients(localPttHandle));

    private static void ObserveProtectedTransmit(Fixture fixture) =>
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "engine-observer",
            ProtectedHandle,
            "TRANSMITTING",
            ProtectedHandle,
            "SW",
            Clients(ProtectedHandle));

    private static void ObserveExternalTransmit(Fixture fixture) =>
        fixture.Occupancy.ObserveInterlock(
            RadioId,
            "independent-observer",
            ObserverHandle,
            "TRANSMITTING",
            ExternalHandle,
            "SW",
            Clients(ExternalHandle));

    private static RadioGuiClientDiagnostics[] Clients(uint localPttHandle) =>
    [
        new RadioGuiClientDiagnostics(
            ProtectedHandle,
            "engine-client",
            "AetherD",
            "AETHER-ENGINE",
            string.Empty,
            localPttHandle == ProtectedHandle,
            false),
        new RadioGuiClientDiagnostics(
            ObserverHandle,
            "safety-observer",
            "AetherSDR Safety",
            "AETHER-SAFETY",
            string.Empty,
            localPttHandle == ObserverHandle,
            true),
        new RadioGuiClientDiagnostics(
            ExternalHandle,
            "smartsdr-client",
            "SmartSDR-Win",
            "EXTERNAL-STATION",
            string.Empty,
            localPttHandle == ExternalHandle,
            false)
    ];

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly RecordingArmAuthority? m_armAuthority;

        public Fixture(
            ManualTimeProvider time,
            RadioTxOccupancyRegistry occupancy,
            FakeEmergencyTransport transport,
            StationTxSafetySupervisor supervisor,
            RecordingArmAuthority? armAuthority)
        {
            Time = time;
            Occupancy = occupancy;
            Transport = transport;
            Supervisor = supervisor;
            m_armAuthority = armAuthority;
            Composition = new StationTxSafetyArmComposition(
                armAuthority,
                supervisor,
                Resolve,
                time);
        }

        public ManualTimeProvider Time { get; }
        public RadioTxOccupancyRegistry Occupancy { get; }
        public FakeEmergencyTransport Transport { get; }
        public StationTxSafetySupervisor Supervisor { get; }
        public StationTxSafetyArmComposition Composition { get; }
        public int ResolveCount { get; private set; }
        public Func<StationTxCommandAuthority, StationTxCommandAuthority>
            AuthorityTransform { get; set; } = authority => authority;

        public ValueTask DisposeAsync() => Supervisor.DisposeAsync();

        private StationTxCommandAuthorityResolution Resolve(
            string? requestedConnectionClientId)
        {
            ResolveCount++;
            if (requestedConnectionClientId is not null &&
                !string.Equals(
                    requestedConnectionClientId.Trim(),
                    ConnectionClientId,
                    StringComparison.Ordinal))
            {
                return StationTxCommandAuthorityResolution.Rejected(
                    "connection_mismatch",
                    "The browser connection is no longer current.");
            }

            StationTxCommandAuthority authority = new(
                StationId,
                RadioId,
                SessionId,
                BrowserClientId,
                LeaseId,
                Time.GetUtcNow() + TimeSpan.FromSeconds(5),
                StationId,
                EngineInstanceId,
                ProtectedHandle,
                Authenticated: true,
                BrowserFresh: true,
                EngineFresh: true,
                GatewayFresh: true,
                AuthorityFresh: true,
                Occupancy.GetSnapshot(RadioId),
                Supervisor.Snapshot);
            return StationTxCommandAuthorityResolution.Accepted(
                AuthorityTransform(authority));
        }
    }

    private sealed class RecordingArmAuthority : IStationTxSafetyArmAuthority
    {
        public StationTxSafetyArmAuthorityCapabilities CapabilitiesValue { get; set; } =
            new(false, false, false, false, "unregistered");
        public bool ThrowCapabilities { get; set; }
        public bool ThrowAuthorization { get; set; }
        public bool CancelAuthorization { get; set; }
        public List<StationTxSafetyArmAuthorizationRequest> Requests { get; } = [];
        public Queue<StationTxSafetyArmAuthorizationResult> Results { get; } = new();

        public StationTxSafetyArmAuthorityCapabilities Capabilities =>
            ThrowCapabilities
                ? throw new InvalidOperationException("capabilities fault")
                : CapabilitiesValue;

        public Task<StationTxSafetyArmAuthorizationResult> AuthorizeAsync(
            StationTxSafetyArmAuthorizationRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (CancelAuthorization)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (ThrowAuthorization)
            {
                throw new InvalidOperationException("authorization fault");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Results.Count > 0
                    ? Results.Dequeue()
                    : StationTxSafetyArmAuthorizationResult.Accepted());
        }
    }

    private sealed class FakeEmergencyTransport : IStationTxEmergencyUnkeyTransport
    {
        public bool IsConnected { get; set; } = true;
        public List<bool> Commands { get; } = [];

        public Task<StationTxTransportResult> RequestUnkeyAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(false);
            return Task.FromResult(StationTxTransportResult.Ok);
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
