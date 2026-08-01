namespace AetherSDR.Web.Radio;

public sealed record BrowserTxTransactionIngressDiagnostics(
    bool Registered,
    bool ExecutionEnabled,
    bool TransactionBoundaryAttached,
    bool KeyAvailable,
    bool UnkeyAvailable,
    long AttemptCount,
    long ForwardedCount,
    long AcceptedCount,
    long RejectedCount,
    long UnknownCount,
    string LastOutcome,
    string LastReason,
    DateTimeOffset? LastObservedAt);

internal enum BrowserTxTransactionIngressOutcome
{
    Accepted = 1,
    Rejected = 2,
    Unknown = 3
}

internal sealed record BrowserTxTransactionIngressRequest(
    string ConnectionClientId,
    BrowserTxRequest Request,
    BrowserTxIntentResult Validation);

internal sealed record BrowserTxTransactionIngressResult(
    BrowserTxTransactionIngressOutcome Outcome,
    string Code,
    string Message,
    BrowserTxTransactionIngressDiagnostics Diagnostics,
    StationTxCommandTransactionResult? TransactionResult)
{
    public bool Success => Outcome == BrowserTxTransactionIngressOutcome.Accepted;

    public bool OutcomeKnown => Outcome != BrowserTxTransactionIngressOutcome.Unknown;
}

/// <summary>
/// Typed boundary between a server-validated browser TX intent and the
/// lifecycle-owned transaction composition. Production constructs this adapter
/// with execution disabled and exposes no WebSocket, HTTP, reconnect, timer,
/// watchdog, or AetherRemote caller.
/// </summary>
internal sealed class BrowserTxTransactionIngress
{
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumValidationAge =
        TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaximumValidationClockSkew =
        TimeSpan.FromSeconds(1);

    private const int MaximumConnectionClientIdLength = 128;

    private readonly object m_gate = new();
    private readonly bool m_executionEnabled;
    private readonly Func<StationTxCommandTransactionCompositionDiagnostics>
        m_transactionSnapshot;
    private readonly Func<StationTxCommandTransactionRequest, CancellationToken,
        Task<StationTxCommandTransactionResult>>? m_submit;
    private readonly TimeProvider m_timeProvider;
    private long m_attemptCount;
    private long m_forwardedCount;
    private long m_acceptedCount;
    private long m_rejectedCount;
    private long m_unknownCount;
    private string m_lastOutcome = "none";
    private string m_lastReason = "execution-disabled";
    private DateTimeOffset? m_lastObservedAt;

    public BrowserTxTransactionIngress(
        bool executionEnabled,
        Func<StationTxCommandTransactionCompositionDiagnostics>
            transactionSnapshot,
        Func<StationTxCommandTransactionRequest, CancellationToken,
            Task<StationTxCommandTransactionResult>>? submit,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transactionSnapshot);
        m_executionEnabled = executionEnabled;
        m_transactionSnapshot = transactionSnapshot;
        m_submit = submit;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BrowserTxTransactionIngressDiagnostics Snapshot
    {
        get
        {
            StationTxCommandTransactionCompositionDiagnostics transaction =
                m_transactionSnapshot();
            lock (m_gate)
            {
                return new BrowserTxTransactionIngressDiagnostics(
                    Registered: true,
                    m_executionEnabled,
                    TransactionBoundaryAttached: m_submit is not null,
                    KeyAvailable:
                        m_executionEnabled &&
                        m_submit is not null &&
                        transaction.KeyAvailable,
                    UnkeyAvailable:
                        m_executionEnabled &&
                        m_submit is not null &&
                        transaction.UnkeyAvailable,
                    m_attemptCount,
                    m_forwardedCount,
                    m_acceptedCount,
                    m_rejectedCount,
                    m_unknownCount,
                    m_lastOutcome,
                    m_lastReason,
                    m_lastObservedAt);
            }
        }
    }

    public async Task<BrowserTxTransactionIngressResult> SubmitAsync(
        BrowserTxTransactionIngressRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (m_gate)
        {
            m_attemptCount++;
        }

        BrowserTxIntent? intent = request.Request.Intent;
        if (!TryValidateRequest(request, intent, out string code, out string message))
        {
            return Record(
                BrowserTxTransactionIngressOutcome.Rejected,
                code,
                message,
                transactionResult: null);
        }

        if (!m_executionEnabled)
        {
            return Record(
                BrowserTxTransactionIngressOutcome.Rejected,
                "ingress-disabled",
                "Browser TX transaction execution is disabled.",
                transactionResult: null);
        }

        if (m_submit is null)
        {
            return Record(
                BrowserTxTransactionIngressOutcome.Rejected,
                "transaction-boundary-unattached",
                "The lifecycle transaction boundary is unavailable.",
                transactionResult: null);
        }

        StationTxCommandTransactionCompositionDiagnostics transaction =
            m_transactionSnapshot();
        bool enabling = intent!.Enabled!.Value;
        if (enabling && !transaction.KeyAvailable)
        {
            return Record(
                BrowserTxTransactionIngressOutcome.Rejected,
                "transaction-key-unavailable",
                transaction.Reason,
                transactionResult: null);
        }
        if (!enabling && !transaction.UnkeyAvailable)
        {
            return Record(
                BrowserTxTransactionIngressOutcome.Rejected,
                "transaction-unkey-unavailable",
                transaction.Reason,
                transactionResult: null);
        }

        StationTxCommandTransactionRequest transactionRequest = new(
            NormalizeConnectionClientId(request.ConnectionClientId),
            request.Request.Sequence,
            intent,
            request.Validation.ObservedAt,
            HeartbeatTimeout);

        lock (m_gate)
        {
            m_forwardedCount++;
        }

        StationTxCommandTransactionResult result =
            await m_submit(transactionRequest, cancellationToken);
        return result.Outcome switch
        {
            StationTxCommandTransactionOutcome.Accepted => Record(
                BrowserTxTransactionIngressOutcome.Accepted,
                result.Code,
                result.Message,
                result),
            StationTxCommandTransactionOutcome.Rejected => Record(
                BrowserTxTransactionIngressOutcome.Rejected,
                result.Code,
                result.Message,
                result),
            StationTxCommandTransactionOutcome.Unknown => Record(
                BrowserTxTransactionIngressOutcome.Unknown,
                result.Code,
                result.Message,
                result),
            _ => throw new InvalidOperationException(
                "An unsupported transaction outcome was returned.")
        };
    }

    private bool TryValidateRequest(
        BrowserTxTransactionIngressRequest request,
        BrowserTxIntent? intent,
        out string code,
        out string message)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionClientId) ||
            request.ConnectionClientId.Trim().Length > MaximumConnectionClientIdLength)
        {
            return Fail(
                "invalid-connection-client-id",
                "A bounded current browser connection identity is required.",
                out code,
                out message);
        }
        if (request.Request.Kind != BrowserTxRequestKind.Intent || intent is null)
        {
            return Fail(
                "intent-required",
                "A parsed browser TX intent is required.",
                out code,
                out message);
        }
        if (!request.Validation.Validated)
        {
            return Fail(
                "validation-required",
                "The browser TX intent must be validated by the server first.",
                out code,
                out message);
        }
        if (request.Validation.Ok ||
            !string.Equals(
                request.Validation.Outcome,
                "transport-unavailable",
                StringComparison.Ordinal) ||
            !request.Validation.Capability.IntentValidationAvailable)
        {
            return Fail(
                "validation-not-authoritative",
                "The validation result does not prove exact current server authority.",
                out code,
                out message);
        }
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        if (request.Validation.ObservedAt > now + MaximumValidationClockSkew ||
            now - request.Validation.ObservedAt > MaximumValidationAge)
        {
            return Fail(
                "validation-stale",
                "The server validation result is outside the bounded freshness window.",
                out code,
                out message);
        }
        if (request.Request.Sequence != request.Validation.Sequence ||
            !string.Equals(
                intent.IntentId,
                request.Validation.IntentId,
                StringComparison.Ordinal) ||
            !string.Equals(
                intent.Action,
                request.Validation.Action,
                StringComparison.Ordinal))
        {
            return Fail(
                "validation-mismatch",
                "The validation result does not match the parsed browser intent.",
                out code,
                out message);
        }
        bool supportedIntent =
            (intent.Kind == BrowserTxIntentKind.Mox &&
                string.Equals(intent.Action, "mox.set", StringComparison.Ordinal)) ||
            (intent.Kind == BrowserTxIntentKind.Ptt &&
                string.Equals(intent.Action, "ptt.set", StringComparison.Ordinal));
        if (!supportedIntent || intent.Enabled is null || intent.Text is not null)
        {
            return Fail(
                "unsupported-intent",
                "Only Boolean MOX and PTT intents are accepted by this boundary.",
                out code,
                out message);
        }

        code = string.Empty;
        message = string.Empty;
        return true;
    }

    private BrowserTxTransactionIngressResult Record(
        BrowserTxTransactionIngressOutcome outcome,
        string code,
        string message,
        StationTxCommandTransactionResult? transactionResult)
    {
        lock (m_gate)
        {
            switch (outcome)
            {
                case BrowserTxTransactionIngressOutcome.Accepted:
                    m_acceptedCount++;
                    break;
                case BrowserTxTransactionIngressOutcome.Rejected:
                    m_rejectedCount++;
                    break;
                case BrowserTxTransactionIngressOutcome.Unknown:
                    m_unknownCount++;
                    break;
                default:
                    throw new InvalidOperationException(
                        "An unsupported ingress outcome was recorded.");
            }
            m_lastOutcome = outcome.ToString().ToLowerInvariant();
            m_lastReason = code;
            m_lastObservedAt = m_timeProvider.GetUtcNow();
        }

        return new BrowserTxTransactionIngressResult(
            outcome,
            code,
            message,
            Snapshot,
            transactionResult);
    }

    private static bool Fail(
        string codeValue,
        string messageValue,
        out string code,
        out string message)
    {
        code = codeValue;
        message = messageValue;
        return false;
    }

    private static string NormalizeConnectionClientId(string value) =>
        value.Trim();
}
