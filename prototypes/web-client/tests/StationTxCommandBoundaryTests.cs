using System.Reflection;
using System.Security.Cryptography;
using AetherSDR.TxWatchdog.Protocol;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class StationTxCommandBoundaryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProductionDefaultsExposeNoCommandCapability()
    {
        StationTxCommandBoundary boundary = new(
            enabled: false,
            "station-a",
            new StationTxUnavailableCommandSignatureVerifier(),
            new StationTxUnavailableCommandAdapter(),
            new ManualTimeProvider(Now));

        StationTxCommandCapabilities capabilities = boundary.Capabilities;

        Assert.Equal(1, capabilities.ProtocolVersion);
        Assert.True(capabilities.BoundaryRegistered);
        Assert.False(capabilities.BoundaryEnabled);
        Assert.False(capabilities.SignatureVerificationAvailable);
        Assert.False(capabilities.CommandAdapterRegistered);
        Assert.False(capabilities.ArmingAvailable);
        Assert.False(capabilities.SetTransmitAvailable);
        Assert.Equal("boundary-disabled", capabilities.Reason);
    }

    [Fact]
    public void BrowserRemoteAndWatchdogSurfacesCannotResolveTheAdapter()
    {
        Assert.False(typeof(IStationTxCommandAdapter).IsPublic);
        Assert.False(typeof(StationTxCommandBoundary).IsPublic);

        AssertNoCommandBoundaryReference(typeof(RadioWebSocketEndpoint));
        AssertNoCommandBoundaryReference(typeof(RemoteRadioIntentRouter));
        AssertNoCommandMethodSurface(typeof(RadioCoordinator));
        AssertNoCommandMethodSurface(typeof(StationTxProductionLifecycle));
        AssertNoCommandMethodSurface(typeof(IStationTxIndependentWatchdog));

        string[] watchdogReferences = typeof(WatchdogProtocol)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain("AetherSDR.Web", watchdogReferences);
    }

    [Fact]
    public void UnsupportedCapabilityVersionFailsClosed()
    {
        using Fixture fixture = CreateFixture();

        StationTxCommandCapabilities capabilities =
            fixture.Boundary.GetCapabilities(2);

        Assert.False(capabilities.BoundaryEnabled);
        Assert.False(capabilities.SignatureVerificationAvailable);
        Assert.False(capabilities.CommandAdapterRegistered);
        Assert.False(capabilities.ArmingAvailable);
        Assert.False(capabilities.SetTransmitAvailable);
        Assert.Equal("unsupported-protocol-version", capabilities.Reason);
    }

    [Fact]
    public async Task ExactSignedCommandReachesOnlyTheStationAdapter()
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope envelope = fixture.Sign(CreateEnvelope());

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(envelope));

        Assert.True(result.Success);
        Assert.Equal("accepted", result.Code);
        StationTxValidatedCommand command = Assert.Single(fixture.Adapter.Commands);
        Assert.Equal(envelope.CommandId, command.CommandId);
        Assert.Equal(envelope.Sequence, command.Sequence);
        Assert.Equal(envelope.StationId, command.StationId);
        Assert.Equal(envelope.RadioId, command.RadioId);
        Assert.Equal(envelope.SessionId, command.SessionId);
        Assert.Equal(envelope.BrowserClientId, command.BrowserClientId);
        Assert.Equal(envelope.LeaseId, command.LeaseId);
        Assert.Equal(envelope.EngineInstanceId, command.EngineInstanceId);
        Assert.Equal(envelope.ClientHandle, command.ClientHandle);
        Assert.True(command.Enabled);
        Assert.Equal("accepted", result.Audit.Outcome);
        Assert.DoesNotContain(envelope.LeaseId, result.Audit.LeaseFingerprint);
    }

    [Fact]
    public async Task ExactAetherOwnedUnkeyReachesOnlyTheStationAdapter()
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope envelope = CreateEnvelope() with
        {
            Enabled = false
        };
        RadioTxOccupant owner = new(
            envelope.ClientHandle,
            "AetherSDR",
            "AETHER-WEB-RX",
            string.Empty,
            AetherOwned: true);
        StationTxCommandAuthority authority = CreateAuthority(envelope) with
        {
            Occupancy = CreateAuthority(envelope).Occupancy with
            {
                State = RadioTxOccupancyState.AetherOwned,
                Occupants = [owner],
                LocalPttOwners = []
            }
        };
        envelope = fixture.Sign(envelope);

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                authority);

        Assert.True(result.Success);
        StationTxValidatedCommand command = Assert.Single(fixture.Adapter.Commands);
        Assert.False(command.Enabled);
        Assert.Equal("accepted", result.Code);
        Assert.Equal("accepted", result.Audit.Outcome);
    }

    [Fact]
    public async Task ExternalOwnerUnkeyNeverReachesTheAdapter()
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope envelope = CreateEnvelope() with
        {
            Enabled = false
        };
        StationTxCommandAuthority authority = CreateAuthority(envelope);
        authority = authority with
        {
            Occupancy = authority.Occupancy with
            {
                State = RadioTxOccupancyState.External,
                Occupants =
                [
                    new RadioTxOccupant(
                        0x22222222,
                        "SmartSDR-Win",
                        "EXTERNAL",
                        string.Empty,
                        AetherOwned: false)
                ],
                LocalPttOwners = []
            }
        };
        envelope = fixture.Sign(envelope);

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                authority);

        Assert.False(result.Success);
        Assert.Equal("unkey_ownership_mismatch", result.Code);
        Assert.Empty(fixture.Adapter.Commands);
        Assert.Equal("rejected", result.Audit.Outcome);
    }

    [Fact]
    public async Task InvalidSignatureNeverReachesTheAdapter()
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope envelope = fixture.Sign(CreateEnvelope());
        char replacement = envelope.Signature[0] == 'A' ? 'B' : 'A';
        envelope = envelope with
        {
            Signature = replacement + envelope.Signature[1..]
        };

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(envelope));

        Assert.False(result.Success);
        Assert.Equal("invalid_signature", result.Code);
        Assert.Empty(fixture.Adapter.Commands);
        Assert.Equal("rejected", result.Audit.Outcome);
    }

    [Theory]
    [InlineData("station", "station_mismatch")]
    [InlineData("radio", "radio_mismatch")]
    [InlineData("session", "session_mismatch")]
    [InlineData("browser", "browser_client_mismatch")]
    [InlineData("lease", "lease_mismatch")]
    [InlineData("gateway", "gateway_instance_mismatch")]
    [InlineData("engine", "engine_instance_mismatch")]
    [InlineData("handle", "client_handle_mismatch")]
    public async Task ExactAuthorityBindingRejectsEveryMismatch(
        string field,
        string expectedCode)
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope envelope = CreateEnvelope();
        envelope = field switch
        {
            "station" => envelope with { StationId = "station-b" },
            "radio" => envelope with { RadioId = "FLEX:OTHER" },
            "session" => envelope with { SessionId = "session-b" },
            "browser" => envelope with { BrowserClientId = "browser-b" },
            "lease" => envelope with { LeaseId = "lease-b" },
            "gateway" => envelope with { GatewayInstanceId = "gateway-b" },
            "engine" => envelope with { EngineInstanceId = "engine-b" },
            "handle" => envelope with { ClientHandle = 0x22222222 },
            _ => throw new InvalidOperationException(field)
        };
        envelope = fixture.Sign(envelope);

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(CreateEnvelope()));

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Theory]
    [InlineData("expired", "command_expired")]
    [InlineData("old", "command_too_old")]
    [InlineData("future", "issued_in_future")]
    [InlineData("lifetime", "invalid_command_lifetime")]
    public async Task EnvelopeTimeBoundsFailClosed(
        string condition,
        string expectedCode)
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope envelope = CreateEnvelope();
        envelope = condition switch
        {
            "expired" => envelope with
            {
                IssuedAt = Now - TimeSpan.FromSeconds(10),
                ExpiresAt = Now
            },
            "old" => envelope with
            {
                IssuedAt = Now - TimeSpan.FromSeconds(31),
                ExpiresAt = Now + TimeSpan.FromSeconds(1)
            },
            "future" => envelope with
            {
                IssuedAt = Now + TimeSpan.FromSeconds(6),
                ExpiresAt = Now + TimeSpan.FromSeconds(10)
            },
            "lifetime" => envelope with
            {
                IssuedAt = Now,
                ExpiresAt = Now + TimeSpan.FromSeconds(16)
            },
            _ => throw new InvalidOperationException(condition)
        };
        envelope = fixture.Sign(envelope);

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(envelope));

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task ReplayedSequenceIsRejectedAfterFirstAcceptance()
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope first = fixture.Sign(CreateEnvelope(sequence: 7));
        StationTxCommandEnvelope replay = fixture.Sign(
            CreateEnvelope(sequence: 7) with
            {
                CommandId = Guid.NewGuid().ToString("N")
            });

        StationTxCommandBoundaryResult accepted =
            await fixture.Boundary.ValidateAndExecuteAsync(
                first,
                CreateAuthority(first));
        StationTxCommandBoundaryResult rejected =
            await fixture.Boundary.ValidateAndExecuteAsync(
                replay,
                CreateAuthority(replay));

        Assert.True(accepted.Success);
        Assert.False(rejected.Success);
        Assert.Equal("stale_sequence", rejected.Code);
        Assert.Single(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task ExpiredAuthoritativeLeaseBlocksTheStationAdapter()
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope envelope = fixture.Sign(CreateEnvelope());
        StationTxCommandAuthority authority = CreateAuthority(envelope) with
        {
            LeaseExpiresAt = Now
        };

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(envelope, authority);

        Assert.False(result.Success);
        Assert.Equal("lease_mismatch", result.Code);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task DisarmedSupervisorBlocksTheStationAdapter()
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope envelope = fixture.Sign(CreateEnvelope());
        StationTxCommandAuthority authority = CreateAuthority(envelope) with
        {
            Safety = DisarmedSafety(envelope)
        };

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(envelope, authority);

        Assert.False(result.Success);
        Assert.Equal("safety_not_armed", result.Code);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task StaleOccupancyBlocksTheStationAdapter()
    {
        using Fixture fixture = CreateFixture();
        StationTxCommandEnvelope envelope = fixture.Sign(CreateEnvelope());
        StationTxCommandAuthority authority = CreateAuthority(envelope);
        authority = authority with
        {
            Occupancy = authority.Occupancy with
            {
                FreshUntil = Now
            }
        };

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(envelope, authority);

        Assert.False(result.Success);
        Assert.Equal("occupancy_stale", result.Code);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task DisabledBoundaryConsumesNoSequenceAndCallsNoAdapter()
    {
        using Fixture fixture = CreateFixture(enabled: false);
        StationTxCommandEnvelope envelope = fixture.Sign(CreateEnvelope());

        StationTxCommandBoundaryResult first =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(envelope));
        StationTxCommandBoundaryResult second =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(envelope));

        Assert.Equal("boundary_disabled", first.Code);
        Assert.Equal("boundary_disabled", second.Code);
        Assert.Empty(fixture.Adapter.Commands);
    }

    [Fact]
    public async Task ConcurrentValidatedCommandsAreSerialized()
    {
        using Fixture fixture = CreateFixture();
        fixture.Adapter.Entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Adapter.Release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StationTxCommandEnvelope first = fixture.Sign(CreateEnvelope(sequence: 1));
        StationTxCommandEnvelope second = fixture.Sign(
            CreateEnvelope(sequence: 2) with
            {
                CommandId = Guid.NewGuid().ToString("N")
            });

        Task<StationTxCommandBoundaryResult> firstTask =
            fixture.Boundary.ValidateAndExecuteAsync(
                first,
                CreateAuthority(first));
        await fixture.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<StationTxCommandBoundaryResult> secondTask =
            fixture.Boundary.ValidateAndExecuteAsync(
                second,
                CreateAuthority(second));
        await Task.Delay(25);

        Assert.Single(fixture.Adapter.Commands);
        Assert.Equal(1, fixture.Adapter.MaximumConcurrentCalls);

        fixture.Adapter.Release.TrySetResult();
        StationTxCommandBoundaryResult[] results =
            await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(2, fixture.Adapter.Commands.Count);
        Assert.Equal(1, fixture.Adapter.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task AdapterCancellationIsAuditedAndNotReportedAsSuccess()
    {
        using Fixture fixture = CreateFixture();
        fixture.Adapter.Entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Adapter.Release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        StationTxCommandEnvelope envelope = fixture.Sign(CreateEnvelope());

        Task<StationTxCommandBoundaryResult> pending =
            fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(envelope),
                cancellation.Token);
        await fixture.Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        StationTxCommandAuditRecord audit = Assert.Single(
            fixture.Boundary.GetRecentAudit());
        Assert.Equal("cancelled", audit.Outcome);
        Assert.Equal("adapter-cancelled", audit.Reason);
    }

    [Fact]
    public async Task AdapterExceptionIsAuditedAndPropagated()
    {
        using Fixture fixture = CreateFixture();
        fixture.Adapter.Exception = new InvalidOperationException("adapter fault");
        StationTxCommandEnvelope envelope = fixture.Sign(CreateEnvelope());

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Boundary.ValidateAndExecuteAsync(
                    envelope,
                    CreateAuthority(envelope)));

        Assert.Equal("adapter fault", exception.Message);
        StationTxCommandAuditRecord audit = Assert.Single(
            fixture.Boundary.GetRecentAudit());
        Assert.Equal("faulted", audit.Outcome);
        Assert.Equal("adapter-exception", audit.Reason);
    }

    [Fact]
    public async Task UnknownAdapterOutcomeIsNotReportedAsSuccess()
    {
        using Fixture fixture = CreateFixture();
        fixture.Adapter.NextResult =
            StationTxTransportResult.Unknown("adapter disconnected after send");
        StationTxCommandEnvelope envelope = fixture.Sign(CreateEnvelope());

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(envelope));

        Assert.False(result.Success);
        Assert.Equal("adapter_outcome_unknown", result.Code);
        Assert.Single(fixture.Adapter.Commands);
        Assert.Equal("rejected", result.Audit.Outcome);
    }

    [Fact]
    public async Task AuditIsBoundedAndNeverStoresRawLeaseSecrets()
    {
        using Fixture fixture = CreateFixture();
        for (int index = 1;
             index <= StationTxCommandBoundary.MaximumAuditRecords + 5;
             index++)
        {
            StationTxCommandEnvelope envelope = CreateEnvelope(sequence: index) with
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Signature = "invalid"
            };
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(envelope));
        }

        IReadOnlyList<StationTxCommandAuditRecord> audit =
            fixture.Boundary.GetRecentAudit(
                StationTxCommandBoundary.MaximumAuditRecords);

        Assert.Equal(StationTxCommandBoundary.MaximumAuditRecords, audit.Count);
        Assert.All(
            audit,
            record =>
            {
                Assert.Equal(16, record.LeaseFingerprint.Length);
                Assert.DoesNotContain("lease-a", record.LeaseFingerprint);
            });
    }

    [Fact]
    public async Task ADifferentSigningKeyCannotAuthorizeACommand()
    {
        using Fixture fixture = CreateFixture();
        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        StationTxCommandEnvelope envelope = Sign(CreateEnvelope(), otherKey);

        StationTxCommandBoundaryResult result =
            await fixture.Boundary.ValidateAndExecuteAsync(
                envelope,
                CreateAuthority(envelope));

        Assert.False(result.Success);
        Assert.Equal("invalid_signature", result.Code);
        Assert.Empty(fixture.Adapter.Commands);
    }

    private static void AssertNoCommandBoundaryReference(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.Static;
        Assert.DoesNotContain(
            type.GetFields(flags),
            field => IsCommandBoundaryType(field.FieldType));
        Assert.DoesNotContain(
            type.GetProperties(flags),
            property => IsCommandBoundaryType(property.PropertyType));
        Assert.DoesNotContain(
            type.GetMethods(flags),
            method =>
                IsCommandBoundaryType(method.ReturnType) ||
                method.GetParameters().Any(
                    parameter => IsCommandBoundaryType(parameter.ParameterType)));
    }

    private static void AssertNoCommandMethodSurface(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.Static;
        Assert.DoesNotContain(
            type.GetMethods(flags),
            method =>
                IsCommandBoundaryType(method.ReturnType) ||
                method.GetParameters().Any(
                    parameter => IsCommandBoundaryType(parameter.ParameterType)));
    }

    private static bool IsCommandBoundaryType(Type type)
    {
        Type candidate = type.IsByRef ? type.GetElementType() ?? type : type;
        return candidate == typeof(IStationTxCommandAdapter) ||
               candidate == typeof(StationTxCommandBoundary) ||
               candidate == typeof(StationTxCommandEnvelope) ||
               candidate == typeof(StationTxValidatedCommand);
    }

    private static Fixture CreateFixture(bool enabled = true)
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        StationTxEcdsaCommandSignatureVerifier verifier = new(
            "station-key-1",
            key.ExportSubjectPublicKeyInfo());
        RecordingAdapter adapter = new();
        StationTxCommandBoundary boundary = new(
            enabled,
            "station-a",
            verifier,
            adapter,
            new ManualTimeProvider(Now));
        return new Fixture(key, verifier, adapter, boundary);
    }

    private static StationTxCommandEnvelope CreateEnvelope(long sequence = 1) =>
        new(
            StationTxCommandBoundary.ProtocolVersion,
            "station-key-1",
            Guid.NewGuid().ToString("N"),
            sequence,
            Now,
            Now + TimeSpan.FromSeconds(10),
            "station-a",
            "FLEX:1121-1104-6700-2912",
            "session-a",
            "browser-a",
            "lease-a",
            "gateway-a",
            "engine-a",
            0x11111111,
            StationTxCommandAction.SetTransmit,
            Enabled: true,
            Signature: string.Empty);

    private static StationTxCommandAuthority CreateAuthority(
        StationTxCommandEnvelope envelope)
    {
        RadioTxOccupant localPttOwner = new(
            envelope.ClientHandle,
            "AetherSDR",
            "AETHER-WEB-RX",
            string.Empty,
            AetherOwned: true);
        RadioTxOccupancySnapshot occupancy = new(
            envelope.RadioId,
            RadioTxOccupancyState.Idle,
            Now,
            Now + TimeSpan.FromSeconds(8),
            Occupants: [],
            LocalPttOwners: [localPttOwner]);
        StationTxSafetySnapshot safety = new(
            envelope.RadioId,
            StationTxSafetyState.Armed,
            "armed",
            envelope.EngineInstanceId,
            envelope.LeaseId,
            envelope.SessionId,
            envelope.BrowserClientId,
            envelope.ClientHandle,
            Now - TimeSpan.FromSeconds(1),
            Now,
            Now + TimeSpan.FromSeconds(2),
            UnkeyDeadlineAt: null,
            UnkeyAttempts: 0,
            SawProtectedTransmit: false);
        return new StationTxCommandAuthority(
            envelope.StationId,
            envelope.RadioId,
            envelope.SessionId,
            envelope.BrowserClientId,
            envelope.LeaseId,
            Now + TimeSpan.FromSeconds(20),
            envelope.GatewayInstanceId,
            envelope.EngineInstanceId,
            envelope.ClientHandle,
            Authenticated: true,
            BrowserFresh: true,
            EngineFresh: true,
            GatewayFresh: true,
            AuthorityFresh: true,
            occupancy,
            safety);
    }

    private static StationTxSafetySnapshot DisarmedSafety(
        StationTxCommandEnvelope envelope) =>
        new(
            envelope.RadioId,
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

    private static StationTxCommandEnvelope Sign(
        StationTxCommandEnvelope envelope,
        ECDsa key)
    {
        byte[] payload = StationTxCommandBoundary.CreateSigningPayload(envelope);
        byte[] signature = key.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return envelope with { Signature = Base64Url(signature) };
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class Fixture(
        ECDsa key,
        StationTxEcdsaCommandSignatureVerifier verifier,
        RecordingAdapter adapter,
        StationTxCommandBoundary boundary) : IDisposable
    {
        public ECDsa Key { get; } = key;
        public StationTxEcdsaCommandSignatureVerifier Verifier { get; } = verifier;
        public RecordingAdapter Adapter { get; } = adapter;
        public StationTxCommandBoundary Boundary { get; } = boundary;

        public StationTxCommandEnvelope Sign(StationTxCommandEnvelope envelope) =>
            StationTxCommandBoundaryTests.Sign(envelope, Key);

        public void Dispose()
        {
            Verifier.Dispose();
            Key.Dispose();
        }
    }

    private sealed class RecordingAdapter : IStationTxCommandAdapter
    {
        private int m_activeCalls;
        private int m_maximumConcurrentCalls;

        public bool IsRegistered { get; set; } = true;
        public bool ArmingAvailable { get; set; } = true;
        public bool SupportsSetTransmit { get; set; } = true;
        public List<StationTxValidatedCommand> Commands { get; } = [];
        public StationTxTransportResult NextResult { get; set; } =
            StationTxTransportResult.Ok;
        public TaskCompletionSource? Entered { get; set; }
        public TaskCompletionSource? Release { get; set; }
        public Exception? Exception { get; set; }
        public int MaximumConcurrentCalls =>
            Volatile.Read(ref m_maximumConcurrentCalls);

        public async Task<StationTxTransportResult> ExecuteAsync(
            StationTxValidatedCommand command,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref m_activeCalls);
            int currentMaximum;
            do
            {
                currentMaximum = Volatile.Read(ref m_maximumConcurrentCalls);
                if (currentMaximum >= active)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(
                       ref m_maximumConcurrentCalls,
                       active,
                       currentMaximum) != currentMaximum);

            try
            {
                Commands.Add(command);
                Entered?.TrySetResult();
                if (Release is not null)
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }
                if (Exception is not null)
                {
                    throw Exception;
                }
                StationTxTransportResult result = NextResult;
                NextResult = StationTxTransportResult.Ok;
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref m_activeCalls);
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
