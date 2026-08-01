using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed class StationTxCommandEnvelopeCoordinatorSettings
{
    public const string SectionName = "StationTxCommandEnvelopeCoordinator";

    public bool SubmissionEnabled { get; set; }
}

public sealed record StationTxCommandEnvelopeCoordinatorDiagnostics(
    bool Registered,
    bool SubmissionEnabled,
    bool SigningAvailable,
    bool SignatureVerificationAvailable,
    bool BoundaryAttached,
    bool BoundaryEnabled,
    bool BoundarySignatureVerificationAvailable,
    bool CommandAdapterRegistered,
    bool ArmingAvailable,
    bool SetTransmitAvailable,
    bool SubmissionAvailable,
    long AttemptCount,
    long SignedEnvelopeCount,
    long AcceptedCount,
    long RejectedCount,
    string LastOutcome,
    DateTimeOffset? LastObservedAt,
    string Reason);

internal sealed record StationTxValidatedOperatorIntent(
    string IntentId,
    long Sequence,
    BrowserTxIntentKind Kind,
    bool Enabled,
    DateTimeOffset ObservedAt);

internal sealed record StationTxCommandEnvelopeSubmissionRequest(
    StationTxValidatedOperatorIntent Intent,
    StationTxCommandAuthority Authority);

internal sealed record StationTxCommandEnvelopeCoordinatorResult(
    bool Success,
    string Code,
    string Message,
    StationTxCommandEnvelopeCoordinatorDiagnostics Diagnostics,
    StationTxCommandBoundaryResult? BoundaryResult);

/// <summary>
/// Station-scoped coordinator that can join a fresh validated operator intent,
/// the private signing authority, the public trust ring, and a caller-owned
/// station command boundary. Production startup registers diagnostics only. No
/// browser, HTTP, WebSocket, AetherRemote, watchdog, timer, lifecycle, adapter,
/// arming, FLEX command, or RF entry point is attached here.
/// </summary>
public sealed class StationTxCommandEnvelopeCoordinator
{
    internal const int MaximumTrackedIntentIds = 256;
    internal const int MaximumTrackedIntentOwners = 128;
    internal static readonly TimeSpan MaximumIntentAge =
        TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumFutureClockSkew =
        TimeSpan.FromSeconds(1);

    private const int MaximumIntentIdLength = 64;
    private const int P256SignatureBytes = 64;

    private readonly object m_gate = new();
    private readonly bool m_submissionEnabled;
    private readonly IStationTxCommandSigner m_signer;
    private readonly IStationTxCommandSignatureVerifier m_verifier;
    private readonly TimeProvider m_timeProvider;
    private readonly Dictionary<string, DateTimeOffset> m_consumedIntentIds =
        new(StringComparer.Ordinal);
    private readonly Dictionary<IntentOwner, IntentSequenceState>
        m_intentSequences = [];
    private StationTxCommandCapabilities? m_lastBoundaryCapabilities;
    private long m_attemptCount;
    private long m_signedEnvelopeCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private string m_lastOutcome = "none";
    private DateTimeOffset? m_lastObservedAt;

    public StationTxCommandEnvelopeCoordinator(
        IOptions<StationTxCommandEnvelopeCoordinatorSettings> options,
        StationTxCommandSigningAuthority signingAuthority,
        StationTxCommandTrustRegistry trustRegistry,
        ILogger<StationTxCommandEnvelopeCoordinator> logger)
        : this(
            GetSettings(options),
            signingAuthority?.Signer ??
                throw new ArgumentNullException(nameof(signingAuthority)),
            trustRegistry?.Verifier ??
                throw new ArgumentNullException(nameof(trustRegistry)),
            logger,
            TimeProvider.System)
    {
    }

    internal StationTxCommandEnvelopeCoordinator(
        StationTxCommandEnvelopeCoordinatorSettings settings,
        IStationTxCommandSigner signer,
        IStationTxCommandSignatureVerifier verifier,
        ILogger<StationTxCommandEnvelopeCoordinator> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        m_submissionEnabled = settings.SubmissionEnabled;
        m_signer = signer;
        m_verifier = verifier;
        m_timeProvider = timeProvider;

        StationTxCommandEnvelopeCoordinatorDiagnostics snapshot = Snapshot;
        logger.LogInformation(
            "Station TX command envelope coordinator {State}; no command " +
            "boundary, adapter, arming path, browser ingress, or radio " +
            "transport is attached",
            snapshot.Reason);
    }

    private static StationTxCommandEnvelopeCoordinatorSettings GetSettings(
        IOptions<StationTxCommandEnvelopeCoordinatorSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Value ??
            new StationTxCommandEnvelopeCoordinatorSettings();
    }

    public StationTxCommandEnvelopeCoordinatorDiagnostics Snapshot
    {
        get
        {
            lock (m_gate)
            {
                return CreateSnapshotLocked();
            }
        }
    }

    internal async Task<StationTxCommandEnvelopeCoordinatorResult> SubmitAsync(
        StationTxCommandEnvelopeSubmissionRequest request,
        StationTxCommandBoundary boundary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Intent);
        ArgumentNullException.ThrowIfNull(request.Authority);
        ArgumentNullException.ThrowIfNull(boundary);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = m_timeProvider.GetUtcNow();
        StationTxCommandCapabilities capabilities = boundary.Capabilities;
        BeginAttempt(capabilities, now);

        CoordinatorFailure? failure = ValidateIntent(request.Intent, now);
        if (failure is null)
        {
            failure = ValidateAuthorityShape(request.Authority);
        }
        if (failure is null && !m_submissionEnabled)
        {
            failure = new(
                "coordinator_disabled",
                "Station-local command envelope submission is disabled.");
        }
        if (failure is null && !m_signer.IsAvailable)
        {
            failure = new(
                "signer_unavailable",
                "The station-local command signing authority is unavailable.");
        }
        if (failure is null && !m_verifier.IsAvailable)
        {
            failure = new(
                "signature_verifier_unavailable",
                "The station-local command signature verifier is unavailable.");
        }
        if (failure is null && !capabilities.BoundaryEnabled)
        {
            failure = new(
                "boundary_disabled",
                "The station-local command boundary is disabled.");
        }
        if (failure is null &&
            !capabilities.SignatureVerificationAvailable)
        {
            failure = new(
                "boundary_signature_verifier_unavailable",
                "The caller-owned command boundary has no signature verifier.");
        }
        if (failure is null && !capabilities.CommandAdapterRegistered)
        {
            failure = new(
                "adapter_unavailable",
                "No station-local command adapter is registered.");
        }
        if (failure is null && !capabilities.ArmingAvailable)
        {
            failure = new(
                "arming_unavailable",
                "The station-local command adapter cannot be armed.");
        }
        if (failure is null && !capabilities.SetTransmitAvailable)
        {
            failure = new(
                "command_unavailable",
                "The station-local SetTransmit command is unavailable.");
        }
        if (failure is null)
        {
            IntentConsumptionResult consumption = TryConsumeIntent(request, now);
            failure = consumption switch
            {
                IntentConsumptionResult.Consumed => null,
                IntentConsumptionResult.Replayed => new(
                    "intent_replayed",
                    "The validated operator intent was already consumed."),
                IntentConsumptionResult.StaleSequence => new(
                    "intent_sequence_replayed",
                    "The validated operator intent sequence is stale or replayed."),
                IntentConsumptionResult.CapacityExceeded => new(
                    "intent_tracking_capacity_exceeded",
                    "The bounded validated-intent replay tracker is full."),
                _ => new(
                    "intent_tracking_failed",
                    "The validated operator intent could not be tracked.")
            };
        }

        if (failure is not null)
        {
            return Reject(failure, capabilities, now);
        }

        cancellationToken.ThrowIfCancellationRequested();
        StationTxCommandEnvelope envelope;
        try
        {
            envelope = m_signer.CreateEnvelope(
                CreateSigningRequest(request));
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return Reject(
                new CoordinatorFailure(
                    "signing_failed",
                    "The station-local signing authority rejected the command request."),
                capabilities,
                now);
        }

        RecordSignedEnvelope(now);
        if (!SelfVerify(envelope))
        {
            return Reject(
                new CoordinatorFailure(
                    "signature_self_verification_failed",
                    "The generated station-local command signature was not trusted."),
                capabilities,
                now);
        }

        StationTxCommandBoundaryResult boundaryResult;
        try
        {
            boundaryResult = await boundary.ValidateAndExecuteAsync(
                envelope,
                request.Authority,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CompleteException("cancelled", now);
            throw;
        }
        catch
        {
            CompleteException("boundary-exception", now);
            throw;
        }

        return Complete(boundaryResult, now);
    }

    private static StationTxCommandSigningRequest CreateSigningRequest(
        StationTxCommandEnvelopeSubmissionRequest request)
    {
        StationTxCommandAuthority authority = request.Authority;
        return new StationTxCommandSigningRequest(
            authority.StationId,
            authority.RadioId,
            authority.SessionId,
            authority.BrowserClientId,
            authority.LeaseId,
            authority.GatewayInstanceId,
            authority.EngineInstanceId,
            authority.ClientHandle,
            StationTxCommandAction.SetTransmit,
            request.Intent.Enabled);
    }

    private static CoordinatorFailure? ValidateIntent(
        StationTxValidatedOperatorIntent intent,
        DateTimeOffset now)
    {
        if (!IsCanonicalIntentId(intent.IntentId))
        {
            return new(
                "invalid_intent_id",
                "The validated operator intent identifier is invalid.");
        }
        if (intent.Sequence <= 0)
        {
            return new(
                "invalid_intent_sequence",
                "The validated operator intent sequence must be positive.");
        }
        if (intent.Kind is not BrowserTxIntentKind.Mox and
            not BrowserTxIntentKind.Ptt)
        {
            return new(
                "unsupported_intent",
                "Only deliberate MOX or PTT intent can request SetTransmit.");
        }
        if (intent.ObservedAt > now + MaximumFutureClockSkew)
        {
            return new(
                "intent_from_future",
                "The validated operator intent timestamp is in the future.");
        }
        if (intent.ObservedAt < now - MaximumIntentAge)
        {
            return new(
                "intent_stale",
                "The validated operator intent is stale.");
        }
        return null;
    }

    private static CoordinatorFailure? ValidateAuthorityShape(
        StationTxCommandAuthority authority)
    {
        if (!IsCanonicalAuthorityIdentifier(authority.StationId) ||
            !IsCanonicalAuthorityIdentifier(authority.RadioId) ||
            !IsCanonicalAuthorityIdentifier(authority.SessionId) ||
            !IsCanonicalAuthorityIdentifier(authority.BrowserClientId) ||
            !IsCanonicalAuthorityIdentifier(authority.LeaseId) ||
            !IsCanonicalAuthorityIdentifier(authority.GatewayInstanceId) ||
            !IsCanonicalAuthorityIdentifier(authority.EngineInstanceId) ||
            authority.ClientHandle == 0)
        {
            return new(
                "invalid_authority",
                "The server-owned station command authority shape is invalid.");
        }
        return null;
    }

    private static bool IsCanonicalAuthorityIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                return false;
            }
        }
        return true;
    }

    private IntentConsumptionResult TryConsumeIntent(
        StationTxCommandEnvelopeSubmissionRequest request,
        DateTimeOffset now)
    {
        StationTxValidatedOperatorIntent intent = request.Intent;
        IntentOwner owner = new(
            request.Authority.SessionId,
            request.Authority.BrowserClientId);
        DateTimeOffset expiresAt =
            intent.ObservedAt + MaximumIntentAge + MaximumFutureClockSkew;

        lock (m_gate)
        {
            PurgeExpiredIntentsLocked(now);
            if (m_consumedIntentIds.ContainsKey(intent.IntentId))
            {
                return IntentConsumptionResult.Replayed;
            }
            if (m_intentSequences.TryGetValue(
                    owner,
                    out IntentSequenceState? previous) &&
                intent.Sequence <= previous.Sequence)
            {
                return IntentConsumptionResult.StaleSequence;
            }
            if (m_consumedIntentIds.Count >= MaximumTrackedIntentIds ||
                (!m_intentSequences.ContainsKey(owner) &&
                 m_intentSequences.Count >= MaximumTrackedIntentOwners))
            {
                return IntentConsumptionResult.CapacityExceeded;
            }

            m_consumedIntentIds.Add(intent.IntentId, expiresAt);
            m_intentSequences[owner] = new IntentSequenceState(
                intent.Sequence,
                expiresAt);
            return IntentConsumptionResult.Consumed;
        }
    }

    private void PurgeExpiredIntentsLocked(DateTimeOffset now)
    {
        foreach (string intentId in m_consumedIntentIds
                     .Where(item => item.Value < now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            m_consumedIntentIds.Remove(intentId);
        }
        foreach (IntentOwner owner in m_intentSequences
                     .Where(item => item.Value.ExpiresAt < now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            m_intentSequences.Remove(owner);
        }
    }

    private bool SelfVerify(StationTxCommandEnvelope envelope)
    {
        if (!TryDecodeSignature(envelope.Signature, out byte[] signature))
        {
            return false;
        }

        try
        {
            byte[] payload = StationTxCommandBoundary.CreateSigningPayload(envelope);
            try
            {
                return m_verifier.Verify(envelope.KeyId, payload, signature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static bool TryDecodeSignature(
        string value,
        out byte[] signature)
    {
        signature = [];
        if (string.IsNullOrEmpty(value) ||
            value.Length > 128 ||
            value.Any(character =>
                !(character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '-' or '_')))
        {
            return false;
        }

        string base64 = value.Replace('-', '+').Replace('_', '/');
        int remainder = base64.Length % 4;
        if (remainder == 1)
        {
            return false;
        }
        if (remainder != 0)
        {
            base64 = base64.PadRight(base64.Length + (4 - remainder), '=');
        }

        try
        {
            signature = Convert.FromBase64String(base64);
            return signature.Length == P256SignatureBytes &&
                   string.Equals(Base64Url(signature), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            signature = [];
            return false;
        }
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool IsCanonicalIntentId(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaximumIntentIdLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!(character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '-' or '_' or '.'))
            {
                return false;
            }
        }
        return true;
    }

    private void BeginAttempt(
        StationTxCommandCapabilities capabilities,
        DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_attemptCount = checked(m_attemptCount + 1);
            m_lastBoundaryCapabilities = capabilities;
            m_lastObservedAt = now;
        }
    }

    private void RecordSignedEnvelope(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_signedEnvelopeCount = checked(m_signedEnvelopeCount + 1);
            m_lastObservedAt = now;
        }
    }

    private StationTxCommandEnvelopeCoordinatorResult Reject(
        CoordinatorFailure failure,
        StationTxCommandCapabilities capabilities,
        DateTimeOffset now)
    {
        StationTxCommandEnvelopeCoordinatorDiagnostics diagnostics;
        lock (m_gate)
        {
            m_lastBoundaryCapabilities = capabilities;
            m_rejectedCount = checked(m_rejectedCount + 1);
            m_lastOutcome = failure.Code;
            m_lastObservedAt = now;
            diagnostics = CreateSnapshotLocked();
        }
        return new StationTxCommandEnvelopeCoordinatorResult(
            Success: false,
            failure.Code,
            failure.Message,
            diagnostics,
            BoundaryResult: null);
    }

    private StationTxCommandEnvelopeCoordinatorResult Complete(
        StationTxCommandBoundaryResult boundaryResult,
        DateTimeOffset now)
    {
        StationTxCommandEnvelopeCoordinatorDiagnostics diagnostics;
        lock (m_gate)
        {
            m_lastBoundaryCapabilities = boundaryResult.Capabilities;
            if (boundaryResult.Success)
            {
                m_acceptedCount = checked(m_acceptedCount + 1);
            }
            else
            {
                m_rejectedCount = checked(m_rejectedCount + 1);
            }
            m_lastOutcome = boundaryResult.Code;
            m_lastObservedAt = now;
            diagnostics = CreateSnapshotLocked();
        }
        return new StationTxCommandEnvelopeCoordinatorResult(
            boundaryResult.Success,
            boundaryResult.Code,
            boundaryResult.Message,
            diagnostics,
            boundaryResult);
    }

    private void CompleteException(string outcome, DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_rejectedCount = checked(m_rejectedCount + 1);
            m_lastOutcome = outcome;
            m_lastObservedAt = now;
        }
    }

    private StationTxCommandEnvelopeCoordinatorDiagnostics CreateSnapshotLocked()
    {
        StationTxCommandCapabilities? capabilities =
            m_lastBoundaryCapabilities;
        bool boundaryAttached = capabilities is not null;
        bool boundaryEnabled = capabilities?.BoundaryEnabled ?? false;
        bool boundaryVerifierAvailable =
            capabilities?.SignatureVerificationAvailable ?? false;
        bool adapterRegistered =
            capabilities?.CommandAdapterRegistered ?? false;
        bool armingAvailable = capabilities?.ArmingAvailable ?? false;
        bool setTransmitAvailable =
            capabilities?.SetTransmitAvailable ?? false;
        bool available =
            m_submissionEnabled &&
            m_signer.IsAvailable &&
            m_verifier.IsAvailable &&
            boundaryAttached &&
            boundaryEnabled &&
            boundaryVerifierAvailable &&
            adapterRegistered &&
            armingAvailable &&
            setTransmitAvailable;

        return new StationTxCommandEnvelopeCoordinatorDiagnostics(
            Registered: true,
            m_submissionEnabled,
            SigningAvailable: m_signer.IsAvailable,
            SignatureVerificationAvailable: m_verifier.IsAvailable,
            boundaryAttached,
            boundaryEnabled,
            boundaryVerifierAvailable,
            adapterRegistered,
            armingAvailable,
            setTransmitAvailable,
            SubmissionAvailable: available,
            m_attemptCount,
            m_signedEnvelopeCount,
            m_acceptedCount,
            m_rejectedCount,
            m_lastOutcome,
            m_lastObservedAt,
            Reason: available ? "available" : AvailabilityReason(capabilities));
    }

    private string AvailabilityReason(
        StationTxCommandCapabilities? capabilities)
    {
        if (!m_submissionEnabled)
        {
            return "disabled";
        }
        if (!m_signer.IsAvailable)
        {
            return "signer-unavailable";
        }
        if (!m_verifier.IsAvailable)
        {
            return "signature-verifier-unavailable";
        }
        if (capabilities is null)
        {
            return "boundary-unbound";
        }
        if (!capabilities.BoundaryEnabled)
        {
            return "boundary-disabled";
        }
        if (!capabilities.SignatureVerificationAvailable)
        {
            return "boundary-signature-verifier-unavailable";
        }
        if (!capabilities.CommandAdapterRegistered)
        {
            return "adapter-unavailable";
        }
        if (!capabilities.ArmingAvailable)
        {
            return "arming-unavailable";
        }
        if (!capabilities.SetTransmitAvailable)
        {
            return "command-unavailable";
        }
        return "unavailable";
    }

    private enum IntentConsumptionResult
    {
        Consumed,
        Replayed,
        StaleSequence,
        CapacityExceeded
    }

    private readonly record struct IntentOwner(
        string SessionId,
        string BrowserClientId);

    private sealed record IntentSequenceState(
        long Sequence,
        DateTimeOffset ExpiresAt);

    private sealed record CoordinatorFailure(string Code, string Message);
}
