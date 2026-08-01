using System.Reflection;
using System.Security.Cryptography;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandEnvelopeCoordinatorTests
{
    private const string KeyId = "station-key-1";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 1, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PublicSurfaceExposesDiagnosticsOnly()
    {
        string[] publicMethods = typeof(StationTxCommandEnvelopeCoordinator)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["get_Snapshot"], publicMethods);
        Assert.False(typeof(StationTxValidatedOperatorIntent).IsPublic);
        Assert.False(typeof(StationTxCommandEnvelopeSubmissionRequest).IsPublic);
        Assert.False(typeof(StationTxCommandEnvelopeCoordinatorResult).IsPublic);
        Assert.Null(typeof(StationTxCommandEnvelopeCoordinator).GetMethod(
            "SubmitAsync",
            BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void SubmissionRequestContainsOnlyValidatedIntentAndAuthority()
    {
        string[] requestProperties =
            typeof(StationTxCommandEnvelopeSubmissionRequest)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
        string[] intentProperties = typeof(StationTxValidatedOperatorIntent)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Authority", "Intent"], requestProperties);
        Assert.Equal(
            ["Enabled", "IntentId", "Kind", "ObservedAt", "Sequence"],
            intentProperties);
        Assert.DoesNotContain(
            requestProperties,
            property => property.Contains("Envelope", StringComparison.Ordinal));
        Assert.DoesNotContain(
            requestProperties,
            property => property.Contains("Signature", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownConfigurationPropertyFailsStrictBinding()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{StationTxCommandEnvelopeCoordinatorSettings.SectionName}:" +
                    "SubmissionEnabled"] = "false",
                [$"{StationTxCommandEnvelopeCoordinatorSettings.SectionName}:" +
                    "Unexpected"] = "true"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            configuration
                .GetSection(
                    StationTxCommandEnvelopeCoordinatorSettings.SectionName)
                .Get<StationTxCommandEnvelopeCoordinatorSettings>(options =>
                    options.ErrorOnUnknownConfiguration = true));
    }

    [Fact]
    public void DefaultCoordinatorIsRegisteredDisabledAndUnbound()
    {
        StationTxCommandEnvelopeCoordinator coordinator = CreateCoordinator(
            enabled: false,
            new AvailableThrowingSigner(),
            new AvailableRejectingVerifier());

        StationTxCommandEnvelopeCoordinatorDiagnostics snapshot =
            coordinator.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.False(snapshot.SubmissionEnabled);
        Assert.True(snapshot.SigningAvailable);
        Assert.True(snapshot.SignatureVerificationAvailable);
        Assert.False(snapshot.BoundaryAttached);
        Assert.False(snapshot.SubmissionAvailable);
        Assert.Equal("disabled", snapshot.Reason);
        Assert.Equal("none", snapshot.LastOutcome);
        Assert.Equal(0, snapshot.AttemptCount);
    }

    [Fact]
    public void EnabledCoordinatorRemainsUnavailableWithoutBoundary()
    {
        StationTxCommandEnvelopeCoordinator coordinator = CreateCoordinator(
            enabled: true,
            new AvailableThrowingSigner(),
            new AvailableRejectingVerifier());

        StationTxCommandEnvelopeCoordinatorDiagnostics snapshot =
            coordinator.Snapshot;

        Assert.True(snapshot.SubmissionEnabled);
        Assert.False(snapshot.BoundaryAttached);
        Assert.False(snapshot.SubmissionAvailable);
        Assert.Equal("boundary-unbound", snapshot.Reason);
    }

    [Fact]
    public void UnavailableSignerFailsClosedBeforeBoundaryAttachment()
    {
        StationTxCommandEnvelopeCoordinator coordinator = CreateCoordinator(
            enabled: true,
            new UnavailableSigner(),
            new AvailableRejectingVerifier());

        Assert.False(coordinator.Snapshot.SigningAvailable);
        Assert.Equal("signer-unavailable", coordinator.Snapshot.Reason);
    }

    [Fact]
    public void UnavailableVerifierFailsClosedBeforeBoundaryAttachment()
    {
        StationTxCommandEnvelopeCoordinator coordinator = CreateCoordinator(
            enabled: true,
            new AvailableThrowingSigner(),
            new UnavailableVerifier());

        Assert.False(coordinator.Snapshot.SignatureVerificationAvailable);
        Assert.Equal(
            "signature-verifier-unavailable",
            coordinator.Snapshot.Reason);
    }

    [Fact]
    public async Task DisabledCoordinatorDoesNotCreateEnvelope()
    {
        using Fixture fixture = new(coordinatorEnabled: false);

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(
                Request(),
                fixture.Boundary);

        Assert.False(result.Success);
        Assert.Equal("coordinator_disabled", result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Theory]
    [InlineData("", "invalid_intent_id")]
    [InlineData(" intent", "invalid_intent_id")]
    [InlineData("intent value", "invalid_intent_id")]
    [InlineData("intent/1", "invalid_intent_id")]
    public async Task InvalidIntentIdFailsBeforeSigning(
        string intentId,
        string expectedCode)
    {
        using Fixture fixture = new();
        StationTxCommandEnvelopeSubmissionRequest request = Request() with
        {
            Intent = Intent() with { IntentId = intentId }
        };

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);

        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task NonPositiveIntentSequenceFailsBeforeSigning()
    {
        using Fixture fixture = new();
        StationTxCommandEnvelopeSubmissionRequest request = Request() with
        {
            Intent = Intent() with { Sequence = 0 }
        };

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);

        Assert.Equal("invalid_intent_sequence", result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
    }

    [Theory]
    [InlineData((int)BrowserTxIntentKind.Tune)]
    [InlineData((int)BrowserTxIntentKind.Microphone)]
    [InlineData((int)BrowserTxIntentKind.Cw)]
    public async Task NonSetTransmitIntentFailsBeforeSigning(int kindValue)
    {
        using Fixture fixture = new();
        StationTxCommandEnvelopeSubmissionRequest request = Request() with
        {
            Intent = Intent() with
            {
                Kind = (BrowserTxIntentKind)kindValue
            }
        };

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);

        Assert.Equal("unsupported_intent", result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
    }

    [Fact]
    public async Task StaleIntentFailsBeforeSigning()
    {
        using Fixture fixture = new();
        StationTxCommandEnvelopeSubmissionRequest request = Request() with
        {
            Intent = Intent() with
            {
                ObservedAt = Now -
                    StationTxCommandEnvelopeCoordinator.MaximumIntentAge -
                    TimeSpan.FromMilliseconds(1)
            }
        };

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);

        Assert.Equal("intent_stale", result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
    }

    [Fact]
    public async Task FutureIntentFailsBeforeSigning()
    {
        using Fixture fixture = new();
        StationTxCommandEnvelopeSubmissionRequest request = Request() with
        {
            Intent = Intent() with
            {
                ObservedAt = Now +
                    StationTxCommandEnvelopeCoordinator.MaximumFutureClockSkew +
                    TimeSpan.FromMilliseconds(1)
            }
        };

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);

        Assert.Equal("intent_from_future", result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
    }

    [Fact]
    public async Task MalformedAuthorityFailsBeforeIntentConsumption()
    {
        using Fixture fixture = new();
        StationTxCommandEnvelopeSubmissionRequest malformed = Request() with
        {
            Authority = Authority() with { SessionId = "bad session" }
        };

        StationTxCommandEnvelopeCoordinatorResult rejected =
            await fixture.Coordinator.SubmitAsync(malformed, fixture.Boundary);
        StationTxCommandEnvelopeCoordinatorResult corrected =
            await fixture.Coordinator.SubmitAsync(Request(), fixture.Boundary);

        Assert.Equal("invalid_authority", rejected.Code);
        Assert.Equal(0, rejected.Diagnostics.SignedEnvelopeCount);
        Assert.True(corrected.Success);
        Assert.Single(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task DisabledBoundaryFailsBeforeSigning()
    {
        using Fixture fixture = new(boundaryEnabled: false);

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(Request(), fixture.Boundary);

        Assert.Equal("boundary_disabled", result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
        Assert.False(result.Diagnostics.SubmissionAvailable);
    }

    [Fact]
    public async Task BoundaryVerifierUnavailableFailsBeforeSigning()
    {
        using Fixture fixture = new();
        StationTxCommandBoundary boundary = new(
            enabled: true,
            "station-a",
            new UnavailableVerifier(),
            fixture.Adapter,
            new ManualTimeProvider(Now));

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(Request(), boundary);

        Assert.Equal("boundary_signature_verifier_unavailable", result.Code);
        Assert.False(
            result.Diagnostics.BoundarySignatureVerificationAvailable);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task MissingAdapterFailsBeforeSigning()
    {
        using Fixture fixture = new();
        fixture.Adapter.IsRegistered = false;

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(Request(), fixture.Boundary);

        Assert.Equal("adapter_unavailable", result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
    }

    [Fact]
    public async Task MissingArmingFailsBeforeSigning()
    {
        using Fixture fixture = new();
        fixture.Adapter.ArmingAvailable = false;

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(Request(), fixture.Boundary);

        Assert.Equal("arming_unavailable", result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
    }

    [Fact]
    public async Task UnsupportedCommandFailsBeforeSigning()
    {
        using Fixture fixture = new();
        fixture.Adapter.SupportsSetTransmit = false;

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(Request(), fixture.Boundary);

        Assert.Equal("command_unavailable", result.Code);
        Assert.Equal(0, result.Diagnostics.SignedEnvelopeCount);
    }

    [Theory]
    [InlineData((int)BrowserTxIntentKind.Mox, true)]
    [InlineData((int)BrowserTxIntentKind.Mox, false)]
    [InlineData((int)BrowserTxIntentKind.Ptt, true)]
    [InlineData((int)BrowserTxIntentKind.Ptt, false)]
    public async Task FreshValidatedIntentCreatesAndExecutesExactSignedCommand(
        int kindValue,
        bool enabled)
    {
        BrowserTxIntentKind kind = (BrowserTxIntentKind)kindValue;
        using Fixture fixture = new();
        StationTxCommandEnvelopeSubmissionRequest request = Request() with
        {
            Intent = Intent() with
            {
                IntentId = $"intent-{kind}-{enabled}",
                Kind = kind,
                Enabled = enabled
            }
        };

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);

        Assert.True(result.Success);
        Assert.Equal("accepted", result.Code);
        StationTxValidatedCommand command = Assert.Single(fixture.Adapter.Commands);
        Assert.Equal(request.Authority.StationId, command.StationId);
        Assert.Equal(request.Authority.RadioId, command.RadioId);
        Assert.Equal(request.Authority.SessionId, command.SessionId);
        Assert.Equal(request.Authority.BrowserClientId, command.BrowserClientId);
        Assert.Equal(request.Authority.LeaseId, command.LeaseId);
        Assert.Equal(
            request.Authority.GatewayInstanceId,
            command.GatewayInstanceId);
        Assert.Equal(request.Authority.EngineInstanceId, command.EngineInstanceId);
        Assert.Equal(request.Authority.ClientHandle, command.ClientHandle);
        Assert.Equal(StationTxCommandAction.SetTransmit, command.Action);
        Assert.Equal(enabled, command.Enabled);
        Assert.Equal(1, result.Diagnostics.AttemptCount);
        Assert.Equal(1, result.Diagnostics.SignedEnvelopeCount);
        Assert.Equal(1, result.Diagnostics.AcceptedCount);
        Assert.Equal(0, result.Diagnostics.RejectedCount);
        Assert.True(result.Diagnostics.SubmissionAvailable);
    }

    [Fact]
    public async Task MismatchedTrustRingRejectsGeneratedSignatureBeforeBoundary()
    {
        using Fixture fixture = new(mismatchedCoordinatorVerifier: true);

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(Request(), fixture.Boundary);

        Assert.False(result.Success);
        Assert.Equal("signature_self_verification_failed", result.Code);
        Assert.Equal(1, result.Diagnostics.SignedEnvelopeCount);
        Assert.Null(result.BoundaryResult);
        Assert.Empty(fixture.Adapter.Commands);
        Assert.Equal(0, fixture.Boundary.AuditCount);
    }

    [Fact]
    public async Task InvalidAuthorityIsSignedButRejectedByBoundary()
    {
        using Fixture fixture = new();
        StationTxCommandEnvelopeSubmissionRequest request = Request() with
        {
            Authority = Authority() with { Authenticated = false }
        };

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);

        Assert.False(result.Success);
        Assert.Equal("authentication_stale", result.Code);
        Assert.NotNull(result.BoundaryResult);
        Assert.Equal(1, result.Diagnostics.SignedEnvelopeCount);
        Assert.Equal(1, fixture.Boundary.AuditCount);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task ConsumedIntentCannotBeReplayed()
    {
        using Fixture fixture = new();
        StationTxCommandEnvelopeSubmissionRequest request = Request();

        StationTxCommandEnvelopeCoordinatorResult first =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);
        StationTxCommandEnvelopeCoordinatorResult second =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal("intent_replayed", second.Code);
        Assert.Equal(2, second.Diagnostics.AttemptCount);
        Assert.Equal(1, second.Diagnostics.SignedEnvelopeCount);
        Assert.Equal(1, second.Diagnostics.AcceptedCount);
        Assert.Equal(1, second.Diagnostics.RejectedCount);
        Assert.Single(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task NewIntentIdCannotReuseTheSameOwnerSequence()
    {
        using Fixture fixture = new();

        StationTxCommandEnvelopeCoordinatorResult first =
            await fixture.Coordinator.SubmitAsync(
                Request() with
                {
                    Intent = Intent() with { IntentId = "intent-first" }
                },
                fixture.Boundary);
        StationTxCommandEnvelopeCoordinatorResult replay =
            await fixture.Coordinator.SubmitAsync(
                Request() with
                {
                    Intent = Intent() with { IntentId = "intent-second" }
                },
                fixture.Boundary);

        Assert.True(first.Success);
        Assert.False(replay.Success);
        Assert.Equal("intent_sequence_replayed", replay.Code);
        Assert.Equal(1, replay.Diagnostics.SignedEnvelopeCount);
        Assert.Single(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task UniqueIntentsProduceIncreasingCommandSequences()
    {
        using Fixture fixture = new();

        StationTxCommandEnvelopeCoordinatorResult first =
            await fixture.Coordinator.SubmitAsync(
                Request() with
                {
                    Intent = Intent() with
                    {
                        IntentId = "intent-1",
                        Sequence = 1
                    }
                },
                fixture.Boundary);
        StationTxCommandEnvelopeCoordinatorResult second =
            await fixture.Coordinator.SubmitAsync(
                Request() with
                {
                    Intent = Intent() with
                    {
                        IntentId = "intent-2",
                        Sequence = 2
                    }
                },
                fixture.Boundary);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, fixture.Adapter.Commands.Count);
        Assert.True(
            fixture.Adapter.Commands[1].Sequence >
            fixture.Adapter.Commands[0].Sequence);
    }

    [Fact]
    public async Task ReplayTrackerRejectsOwnerCapacityWithoutUnboundedGrowth()
    {
        using Fixture fixture = new();
        for (int index = 0;
             index < StationTxCommandEnvelopeCoordinator.MaximumTrackedIntentOwners;
             index++)
        {
            StationTxCommandEnvelopeCoordinatorResult accepted =
                await fixture.Coordinator.SubmitAsync(
                    new StationTxCommandEnvelopeSubmissionRequest(
                        Intent() with
                        {
                            IntentId = $"intent-{index}",
                            Sequence = index + 1
                        },
                        Authority(
                            $"session-{index}",
                            $"browser-{index}")),
                    fixture.Boundary);
            Assert.True(accepted.Success);
        }

        StationTxCommandEnvelopeCoordinatorResult rejected =
            await fixture.Coordinator.SubmitAsync(
                new StationTxCommandEnvelopeSubmissionRequest(
                    Intent() with
                    {
                        IntentId = "intent-over-capacity",
                        Sequence =
                            StationTxCommandEnvelopeCoordinator
                                .MaximumTrackedIntentOwners + 1
                    },
                    Authority("session-over-capacity", "browser-over-capacity")),
                fixture.Boundary);

        Assert.Equal("intent_tracking_capacity_exceeded", rejected.Code);
        Assert.Equal(
            StationTxCommandEnvelopeCoordinator.MaximumTrackedIntentOwners,
            rejected.Diagnostics.SignedEnvelopeCount);
        Assert.Equal(
            StationTxCommandEnvelopeCoordinator.MaximumTrackedIntentOwners,
            fixture.Adapter.Commands.Count);
    }

    [Fact]
    public async Task KnownAdapterRejectionRemainsRejected()
    {
        using Fixture fixture = new();
        fixture.Adapter.NextResult =
            StationTxTransportResult.Rejected("adapter refused");

        StationTxCommandEnvelopeCoordinatorResult result =
            await fixture.Coordinator.SubmitAsync(Request(), fixture.Boundary);

        Assert.False(result.Success);
        Assert.Equal("adapter_rejected", result.Code);
        Assert.Equal(1, result.Diagnostics.RejectedCount);
        Assert.Equal("adapter_rejected", result.Diagnostics.LastOutcome);
    }

    [Fact]
    public async Task UnknownAdapterOutcomeRemainsUnknownAndIsNotRetried()
    {
        using Fixture fixture = new();
        fixture.Adapter.NextResult =
            StationTxTransportResult.Unknown("outcome unknown");
        StationTxCommandEnvelopeSubmissionRequest request = Request();

        StationTxCommandEnvelopeCoordinatorResult first =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);
        StationTxCommandEnvelopeCoordinatorResult second =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);

        Assert.False(first.Success);
        Assert.Equal("adapter_outcome_unknown", first.Code);
        Assert.Equal("intent_replayed", second.Code);
        Assert.Single(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task CancellationBeforeSubmissionHasNoSideEffects()
    {
        using Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Coordinator.SubmitAsync(
                Request(),
                fixture.Boundary,
                cancellation.Token));

        Assert.Equal(0, fixture.Coordinator.Snapshot.AttemptCount);
        Assert.Equal(0, fixture.Coordinator.Snapshot.SignedEnvelopeCount);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task CancellationDuringAdapterConsumesIntentAndDoesNotRetry()
    {
        using Fixture fixture = new();
        fixture.Adapter.BlockUntilCancelled = true;
        StationTxCommandEnvelopeSubmissionRequest request = Request();
        using CancellationTokenSource cancellation = new();

        Task<StationTxCommandEnvelopeCoordinatorResult> pending =
            fixture.Coordinator.SubmitAsync(
                request,
                fixture.Boundary,
                cancellation.Token);
        await fixture.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("cancelled", fixture.Coordinator.Snapshot.LastOutcome);
        Assert.Equal(1, fixture.Coordinator.Snapshot.RejectedCount);

        fixture.Adapter.BlockUntilCancelled = false;
        StationTxCommandEnvelopeCoordinatorResult replay =
            await fixture.Coordinator.SubmitAsync(request, fixture.Boundary);
        Assert.Equal("intent_replayed", replay.Code);
        Assert.Single(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task CoordinatorIsNotInjectedIntoRadioSessionsOrLifecycle()
    {
        Type coordinatorType = typeof(StationTxCommandEnvelopeCoordinator);
        Assembly assembly = coordinatorType.Assembly;
        Type[] productionTypes = assembly.GetTypes()
            .Where(type =>
                type.Namespace == typeof(RadioSessionRegistry).Namespace &&
                (type == typeof(RadioSessionRegistry) ||
                 type == typeof(StationTxProductionLifecycle)))
            .ToArray();

        foreach (Type type in productionTypes)
        {
            Assert.DoesNotContain(
                type.GetConstructors(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    .SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType == coordinatorType);
            Assert.DoesNotContain(
                type.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic),
                field => field.FieldType == coordinatorType);
        }

        using Fixture fixture = new();
        Assert.False(fixture.Coordinator.Snapshot.BoundaryAttached);
        await Task.CompletedTask;
    }

    private static StationTxCommandEnvelopeCoordinator CreateCoordinator(
        bool enabled,
        IStationTxCommandSigner signer,
        IStationTxCommandSignatureVerifier verifier) =>
        new(
            new StationTxCommandEnvelopeCoordinatorSettings
            {
                SubmissionEnabled = enabled
            },
            signer,
            verifier,
            NullLogger<StationTxCommandEnvelopeCoordinator>.Instance,
            new ManualTimeProvider(Now));

    private static StationTxCommandEnvelopeSubmissionRequest Request() =>
        new(Intent(), Authority());

    private static StationTxValidatedOperatorIntent Intent() =>
        new(
            "intent-1",
            Sequence: 1,
            BrowserTxIntentKind.Mox,
            Enabled: true,
            Now);

    private static StationTxCommandAuthority Authority(
        string sessionId = "session-a",
        string browserClientId = "browser-a")
    {
        const string radioId = "FLEX:1121-1104-6700-2912";
        const uint clientHandle = 0x11111111;
        RadioTxOccupant localPttOwner = new(
            clientHandle,
            "AetherSDR",
            "AETHER-WEB-RX",
            string.Empty,
            AetherOwned: true);
        RadioTxOccupancySnapshot occupancy = new(
            radioId,
            RadioTxOccupancyState.Idle,
            Now,
            Now + TimeSpan.FromSeconds(8),
            Occupants: [],
            LocalPttOwners: [localPttOwner]);
        StationTxSafetySnapshot safety = new(
            radioId,
            StationTxSafetyState.Armed,
            "armed",
            "engine-a",
            "lease-a",
            sessionId,
            browserClientId,
            clientHandle,
            Now - TimeSpan.FromSeconds(1),
            Now,
            Now + TimeSpan.FromSeconds(2),
            UnkeyDeadlineAt: null,
            UnkeyAttempts: 0,
            SawProtectedTransmit: false);
        return new StationTxCommandAuthority(
            "station-a",
            radioId,
            sessionId,
            browserClientId,
            "lease-a",
            Now + TimeSpan.FromSeconds(20),
            "gateway-a",
            "engine-a",
            clientHandle,
            Authenticated: true,
            BrowserFresh: true,
            EngineFresh: true,
            GatewayFresh: true,
            AuthorityFresh: true,
            occupancy,
            safety);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly StationTxEcdsaCommandSigner m_signer;
        private readonly StationTxEcdsaCommandSignatureVerifier
            m_coordinatorVerifier;
        private readonly StationTxEcdsaCommandSignatureVerifier m_boundaryVerifier;

        public Fixture(
            bool coordinatorEnabled = true,
            bool boundaryEnabled = true,
            bool mismatchedCoordinatorVerifier = false)
        {
            ManualTimeProvider timeProvider = new(Now);
            ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] signingPublicKey = signingKey.ExportSubjectPublicKeyInfo();
            m_signer = new StationTxEcdsaCommandSigner(
                enabled: true,
                new StationTxCommandSigningAuthority.LoadedSigningKey(
                    KeyId,
                    "TEST-FINGERPRINT",
                    signingKey),
                timeProvider);

            byte[] coordinatorPublicKey = signingPublicKey;
            ECDsa? mismatchedKey = null;
            if (mismatchedCoordinatorVerifier)
            {
                mismatchedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                coordinatorPublicKey =
                    mismatchedKey.ExportSubjectPublicKeyInfo();
            }

            try
            {
                m_coordinatorVerifier =
                    new StationTxEcdsaCommandSignatureVerifier(
                        KeyId,
                        coordinatorPublicKey);
                m_boundaryVerifier =
                    new StationTxEcdsaCommandSignatureVerifier(
                        KeyId,
                        signingPublicKey);
            }
            finally
            {
                mismatchedKey?.Dispose();
                if (!ReferenceEquals(coordinatorPublicKey, signingPublicKey))
                {
                    CryptographicOperations.ZeroMemory(coordinatorPublicKey);
                }
                CryptographicOperations.ZeroMemory(signingPublicKey);
            }

            Coordinator = new StationTxCommandEnvelopeCoordinator(
                new StationTxCommandEnvelopeCoordinatorSettings
                {
                    SubmissionEnabled = coordinatorEnabled
                },
                m_signer,
                m_coordinatorVerifier,
                NullLogger<StationTxCommandEnvelopeCoordinator>.Instance,
                timeProvider);
            Adapter = new RecordingAdapter();
            Boundary = new StationTxCommandBoundary(
                boundaryEnabled,
                "station-a",
                m_boundaryVerifier,
                Adapter,
                timeProvider);
        }

        public StationTxCommandEnvelopeCoordinator Coordinator { get; }
        public RecordingAdapter Adapter { get; }
        public StationTxCommandBoundary Boundary { get; }

        public void Dispose()
        {
            m_boundaryVerifier.Dispose();
            m_coordinatorVerifier.Dispose();
            m_signer.Dispose();
        }
    }

    private sealed class RecordingAdapter : IStationTxCommandAdapter
    {
        public bool IsRegistered { get; set; } = true;
        public bool ArmingAvailable { get; set; } = true;
        public bool SupportsSetTransmit { get; set; } = true;
        public List<StationTxValidatedCommand> Commands { get; } = [];
        public StationTxTransportResult NextResult { get; set; } =
            StationTxTransportResult.Ok;
        public bool BlockUntilCancelled { get; set; }
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StationTxTransportResult> ExecuteAsync(
            StationTxValidatedCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            Entered.TrySetResult();
            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return NextResult;
        }
    }

    private sealed class AvailableThrowingSigner : IStationTxCommandSigner
    {
        public bool IsAvailable => true;

        public StationTxCommandEnvelope CreateEnvelope(
            StationTxCommandSigningRequest request) =>
            throw new InvalidOperationException("Not expected in this test.");
    }

    private sealed class UnavailableSigner : IStationTxCommandSigner
    {
        public bool IsAvailable => false;

        public StationTxCommandEnvelope CreateEnvelope(
            StationTxCommandSigningRequest request) =>
            throw new InvalidOperationException("Unavailable signer was called.");
    }

    private sealed class AvailableRejectingVerifier :
        IStationTxCommandSignatureVerifier
    {
        public bool IsAvailable => true;

        public bool Verify(
            string keyId,
            ReadOnlySpan<byte> payload,
            ReadOnlySpan<byte> signature) => false;
    }

    private sealed class UnavailableVerifier :
        IStationTxCommandSignatureVerifier
    {
        public bool IsAvailable => false;

        public bool Verify(
            string keyId,
            ReadOnlySpan<byte> payload,
            ReadOnlySpan<byte> signature) => false;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
