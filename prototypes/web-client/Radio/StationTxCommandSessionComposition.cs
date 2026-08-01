namespace AetherSDR.Web.Radio;

public sealed record StationTxCommandSessionCompositionDiagnostics(
    bool Registered,
    bool CoordinatorAttached,
    bool BoundaryAttached,
    bool SubmissionEnabled,
    bool SigningAvailable,
    bool SignatureVerificationAvailable,
    bool BoundaryEnabled,
    bool BoundarySignatureVerificationAvailable,
    bool CommandAdapterRegistered,
    bool ArmingAvailable,
    bool SetTransmitAvailable,
    bool AuthoritySnapshotAvailable,
    bool SubmissionAvailable,
    long AttemptCount,
    long ForwardedCount,
    long AcceptedCount,
    long RejectedCount,
    string LastOutcome,
    DateTimeOffset? LastObservedAt,
    string Reason);

internal sealed record StationTxCommandSessionCompositionRequest(
    string ConnectionClientId,
    long Sequence,
    BrowserTxIntent Intent,
    DateTimeOffset ObservedAt);

internal sealed record StationTxCommandAuthorityResolution(
    bool Success,
    string Code,
    string Message,
    StationTxCommandAuthority? Authority)
{
    public static StationTxCommandAuthorityResolution Accepted(
        StationTxCommandAuthority authority) =>
        new(
            Success: true,
            Code: "ready",
            Message: "The exact session-owned station command authority is ready.",
            authority);

    public static StationTxCommandAuthorityResolution Rejected(
        string code,
        string message) =>
        new(
            Success: false,
            code,
            message,
            Authority: null);
}

internal sealed record StationTxCommandSessionCompositionResult(
    bool Success,
    string Code,
    string Message,
    StationTxCommandSessionCompositionDiagnostics Diagnostics,
    StationTxCommandEnvelopeCoordinatorResult? CoordinatorResult);

internal interface IStationTxCommandTransactionSubmissionParticipant
{
    StationTxCommandSessionCompositionDiagnostics Snapshot { get; }

    Task<StationTxCommandSessionCompositionResult> SubmitAsync(
        StationTxCommandSessionCompositionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-session composition boundary for a validated browser TX intent. It owns
/// no key, adapter, arming operation, browser route, or radio transport. The
/// caller may supply only the current connection identity, the parsed intent,
/// its browser sequence, and the server observation time. All command authority
/// fields are resolved from the owning production lifecycle.
/// </summary>
internal sealed class StationTxCommandSessionComposition :
    IStationTxCommandTransactionSubmissionParticipant
{
    private const int MaximumConnectionIdLength = 128;

    private readonly object m_gate = new();
    private readonly IStationTxCommandEnvelopeSubmitter? m_submitter;
    private readonly StationTxCommandBoundary m_boundary;
    private readonly Func<string?, StationTxCommandAuthorityResolution>
        m_authorityResolver;
    private readonly TimeProvider m_timeProvider;
    private long m_attemptCount;
    private long m_forwardedCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private string m_lastOutcome = "none";
    private DateTimeOffset? m_lastObservedAt;

    public StationTxCommandSessionComposition(
        IStationTxCommandEnvelopeSubmitter? submitter,
        StationTxCommandBoundary boundary,
        Func<string?, StationTxCommandAuthorityResolution> authorityResolver,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(authorityResolver);

        m_submitter = submitter;
        m_boundary = boundary;
        m_authorityResolver = authorityResolver;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public StationTxCommandSessionCompositionDiagnostics Snapshot
    {
        get
        {
            StationTxCommandAuthorityResolution authority =
                ResolveAuthority(connectionClientId: null);
            StationTxCommandEnvelopeCoordinatorDiagnostics? coordinator =
                m_submitter?.Snapshot;
            StationTxCommandCapabilities boundary = m_boundary.Capabilities;
            bool submissionAvailable =
                coordinator is not null &&
                coordinator.SubmissionEnabled &&
                coordinator.SigningAvailable &&
                coordinator.SignatureVerificationAvailable &&
                boundary.BoundaryEnabled &&
                boundary.SignatureVerificationAvailable &&
                boundary.CommandAdapterRegistered &&
                boundary.ArmingAvailable &&
                boundary.SetTransmitAvailable &&
                authority.Success;
            string reason = GetReason(coordinator, boundary, authority);

            lock (m_gate)
            {
                return new StationTxCommandSessionCompositionDiagnostics(
                    Registered: true,
                    CoordinatorAttached: coordinator is not null,
                    BoundaryAttached: true,
                    SubmissionEnabled: coordinator?.SubmissionEnabled == true,
                    SigningAvailable: coordinator?.SigningAvailable == true,
                    SignatureVerificationAvailable:
                        coordinator?.SignatureVerificationAvailable == true,
                    BoundaryEnabled: boundary.BoundaryEnabled,
                    BoundarySignatureVerificationAvailable:
                        boundary.SignatureVerificationAvailable,
                    CommandAdapterRegistered:
                        boundary.CommandAdapterRegistered,
                    ArmingAvailable: boundary.ArmingAvailable,
                    SetTransmitAvailable: boundary.SetTransmitAvailable,
                    AuthoritySnapshotAvailable: authority.Success,
                    SubmissionAvailable: submissionAvailable,
                    m_attemptCount,
                    m_forwardedCount,
                    m_acceptedCount,
                    m_rejectedCount,
                    m_lastOutcome,
                    m_lastObservedAt,
                    reason);
            }
        }
    }

    public async Task<StationTxCommandSessionCompositionResult> SubmitAsync(
        StationTxCommandSessionCompositionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Intent);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = m_timeProvider.GetUtcNow();
        BeginAttempt(now);

        CompositionFailure? failure = ValidateRequest(request);
        if (failure is null && m_submitter is null)
        {
            failure = new(
                "coordinator_unattached",
                "No station command envelope coordinator is attached.");
        }

        StationTxCommandAuthorityResolution? authority = null;
        if (failure is null)
        {
            authority = ResolveAuthority(request.ConnectionClientId);
            if (!authority.Success)
            {
                failure = new(authority.Code, authority.Message);
            }
        }

        if (failure is not null)
        {
            return Reject(failure, now);
        }

        StationTxCommandEnvelopeSubmissionRequest submission = new(
            new StationTxValidatedOperatorIntent(
                request.Intent.IntentId,
                request.Sequence,
                request.Intent.Kind,
                request.Intent.Enabled!.Value,
                request.ObservedAt),
            authority!.Authority!);

        RecordForwarded(now);
        StationTxCommandEnvelopeCoordinatorResult coordinatorResult;
        try
        {
            coordinatorResult = await m_submitter!.SubmitAsync(
                submission,
                m_boundary,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordException("cancelled", now);
            throw;
        }
        catch
        {
            RecordException("submitter-exception", now);
            throw;
        }

        return Complete(coordinatorResult, now);
    }

    private static CompositionFailure? ValidateRequest(
        StationTxCommandSessionCompositionRequest request)
    {
        string connectionClientId = request.ConnectionClientId?.Trim() ??
            string.Empty;
        if (connectionClientId.Length is 0 or > MaximumConnectionIdLength ||
            connectionClientId.Any(char.IsControl))
        {
            return new(
                "invalid_connection_client_id",
                "The live browser connection identity is invalid.");
        }
        if (request.Sequence <= 0 ||
            request.Sequence > RadioBrowserTxProtocol.MaximumSafeInteger)
        {
            return new(
                "invalid_intent_sequence",
                "The validated browser intent sequence is invalid.");
        }
        if (request.Intent.Kind is not BrowserTxIntentKind.Mox and
            not BrowserTxIntentKind.Ptt)
        {
            return new(
                "unsupported_intent",
                "Only deliberate MOX or PTT intent can request SetTransmit.");
        }
        if (!request.Intent.Enabled.HasValue)
        {
            return new(
                "missing_intent_value",
                "The deliberate MOX or PTT intent requires a Boolean value.");
        }
        return null;
    }

    private StationTxCommandAuthorityResolution ResolveAuthority(
        string? connectionClientId)
    {
        try
        {
            return m_authorityResolver(connectionClientId);
        }
        catch
        {
            return StationTxCommandAuthorityResolution.Rejected(
                "authority_resolution_failed",
                "The session-owned station command authority could not be resolved.");
        }
    }

    private static string GetReason(
        StationTxCommandEnvelopeCoordinatorDiagnostics? coordinator,
        StationTxCommandCapabilities boundary,
        StationTxCommandAuthorityResolution authority)
    {
        if (coordinator is null)
        {
            return "coordinator-unattached";
        }
        if (!coordinator.SubmissionEnabled)
        {
            return "submission-disabled";
        }
        if (!coordinator.SigningAvailable)
        {
            return "signer-unavailable";
        }
        if (!coordinator.SignatureVerificationAvailable)
        {
            return "signature-verifier-unavailable";
        }
        if (!boundary.BoundaryEnabled)
        {
            return "boundary-disabled";
        }
        if (!boundary.SignatureVerificationAvailable)
        {
            return "boundary-signature-verifier-unavailable";
        }
        if (!boundary.CommandAdapterRegistered)
        {
            return "adapter-unavailable";
        }
        if (!boundary.ArmingAvailable)
        {
            return "arming-unavailable";
        }
        if (!boundary.SetTransmitAvailable)
        {
            return "set-transmit-unavailable";
        }
        return authority.Success ? "ready" : authority.Code;
    }

    private void BeginAttempt(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_attemptCount++;
            m_lastObservedAt = now;
            m_lastOutcome = "attempting";
        }
    }

    private void RecordForwarded(DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_forwardedCount++;
            m_lastObservedAt = now;
            m_lastOutcome = "forwarded";
        }
    }

    private void RecordException(string outcome, DateTimeOffset now)
    {
        lock (m_gate)
        {
            m_rejectedCount++;
            m_lastObservedAt = now;
            m_lastOutcome = outcome;
        }
    }

    private StationTxCommandSessionCompositionResult Reject(
        CompositionFailure failure,
        DateTimeOffset now)
    {
        StationTxCommandSessionCompositionDiagnostics diagnostics;
        lock (m_gate)
        {
            m_rejectedCount++;
            m_lastObservedAt = now;
            m_lastOutcome = failure.Code;
        }
        diagnostics = Snapshot;
        return new StationTxCommandSessionCompositionResult(
            Success: false,
            failure.Code,
            failure.Message,
            diagnostics,
            CoordinatorResult: null);
    }

    private StationTxCommandSessionCompositionResult Complete(
        StationTxCommandEnvelopeCoordinatorResult coordinatorResult,
        DateTimeOffset now)
    {
        StationTxCommandSessionCompositionDiagnostics diagnostics;
        lock (m_gate)
        {
            if (coordinatorResult.Success)
            {
                m_acceptedCount++;
            }
            else
            {
                m_rejectedCount++;
            }
            m_lastObservedAt = now;
            m_lastOutcome = coordinatorResult.Code;
        }
        diagnostics = Snapshot;
        return new StationTxCommandSessionCompositionResult(
            coordinatorResult.Success,
            coordinatorResult.Code,
            coordinatorResult.Message,
            diagnostics,
            coordinatorResult);
    }

    private sealed record CompositionFailure(string Code, string Message);
}
