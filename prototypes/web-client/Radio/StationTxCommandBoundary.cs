using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AetherSDR.Web.Radio;

internal enum StationTxCommandAction
{
    SetTransmit = 1
}

internal sealed record StationTxCommandEnvelope(
    int ProtocolVersion,
    string KeyId,
    string CommandId,
    long Sequence,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string StationId,
    string RadioId,
    string SessionId,
    string BrowserClientId,
    string LeaseId,
    string GatewayInstanceId,
    string EngineInstanceId,
    uint ClientHandle,
    StationTxCommandAction Action,
    bool Enabled,
    string Signature);

internal sealed record StationTxCommandAuthority(
    string StationId,
    string RadioId,
    string SessionId,
    string BrowserClientId,
    string LeaseId,
    DateTimeOffset LeaseExpiresAt,
    string GatewayInstanceId,
    string EngineInstanceId,
    uint ClientHandle,
    bool Authenticated,
    bool BrowserFresh,
    bool EngineFresh,
    bool GatewayFresh,
    bool AuthorityFresh,
    RadioTxOccupancySnapshot Occupancy,
    StationTxSafetySnapshot Safety);

internal sealed record StationTxCommandCapabilities(
    int ProtocolVersion,
    bool BoundaryRegistered,
    bool BoundaryEnabled,
    bool SignatureVerificationAvailable,
    bool CommandAdapterRegistered,
    bool ArmingAvailable,
    bool SetTransmitAvailable,
    string Reason);

internal sealed record StationTxValidatedCommand(
    string CommandId,
    long Sequence,
    string StationId,
    string RadioId,
    string SessionId,
    string BrowserClientId,
    string LeaseId,
    string GatewayInstanceId,
    string EngineInstanceId,
    uint ClientHandle,
    StationTxCommandAction Action,
    bool Enabled,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

internal sealed record StationTxCommandAuditRecord(
    string CommandId,
    long Sequence,
    string KeyId,
    string StationId,
    string RadioId,
    string SessionId,
    string BrowserClientId,
    string LeaseFingerprint,
    string EngineInstanceId,
    uint ClientHandle,
    StationTxCommandAction Action,
    bool Enabled,
    string Outcome,
    string Reason,
    DateTimeOffset ObservedAt);

internal sealed record StationTxCommandBoundaryResult(
    bool Success,
    string Code,
    string Message,
    StationTxCommandCapabilities Capabilities,
    StationTxCommandAuditRecord Audit);

internal interface IStationTxCommandSignatureVerifier
{
    bool IsAvailable { get; }

    bool Verify(
        string keyId,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature);
}

internal interface IStationTxCommandAdapter
{
    bool IsRegistered { get; }
    bool ArmingAvailable { get; }
    bool SupportsSetTransmit { get; }

    Task<StationTxTransportResult> ExecuteAsync(
        StationTxValidatedCommand command,
        CancellationToken cancellationToken);
}

internal sealed class StationTxCommandBoundary
{
    internal const int ProtocolVersion = 1;
    internal const int MaximumAuditRecords = 256;
    internal static readonly TimeSpan MaximumClockSkew =
        TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumEnvelopeLifetime =
        TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MaximumEnvelopeAge =
        TimeSpan.FromSeconds(30);

    private const int MaximumIdentifierLength = 128;
    private const int MaximumKeyIdLength = 64;
    private const int MaximumSignatureBytes = 96;

    private readonly object m_gate = new();
    private readonly SemaphoreSlim m_executionGate = new(1, 1);
    private readonly bool m_enabled;
    private readonly string m_stationId;
    private readonly IStationTxCommandSignatureVerifier m_verifier;
    private readonly IStationTxCommandAdapter m_adapter;
    private readonly TimeProvider m_timeProvider;
    private readonly List<StationTxCommandAuditRecord> m_audit = [];
    private long m_lastAcceptedSequence;

    public StationTxCommandBoundary(
        bool enabled,
        string stationId,
        IStationTxCommandSignatureVerifier verifier,
        IStationTxCommandAdapter adapter,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(adapter);
        m_stationId = RequireIdentifier(
            stationId,
            nameof(stationId),
            MaximumIdentifierLength);
        m_enabled = enabled;
        m_verifier = verifier;
        m_adapter = adapter;
        m_timeProvider = timeProvider ?? TimeProvider.System;
    }

    public StationTxCommandCapabilities Capabilities =>
        GetCapabilities(ProtocolVersion);

    public StationTxCommandCapabilities GetCapabilities(int protocolVersion)
    {
        if (protocolVersion != ProtocolVersion)
        {
            return new StationTxCommandCapabilities(
                ProtocolVersion,
                BoundaryRegistered: true,
                BoundaryEnabled: false,
                SignatureVerificationAvailable: false,
                CommandAdapterRegistered: false,
                ArmingAvailable: false,
                SetTransmitAvailable: false,
                Reason: "unsupported-protocol-version");
        }

        bool adapterRegistered = m_adapter.IsRegistered;
        bool armingAvailable = adapterRegistered && m_adapter.ArmingAvailable;
        bool available =
            m_enabled &&
            m_verifier.IsAvailable &&
            adapterRegistered &&
            armingAvailable &&
            m_adapter.SupportsSetTransmit;
        return new StationTxCommandCapabilities(
            ProtocolVersion,
            BoundaryRegistered: true,
            BoundaryEnabled: m_enabled,
            SignatureVerificationAvailable: m_verifier.IsAvailable,
            CommandAdapterRegistered: adapterRegistered,
            ArmingAvailable: armingAvailable,
            SetTransmitAvailable: available,
            Reason: available ? "available" : CapabilityReason());
    }

    public int AuditCount
    {
        get
        {
            lock (m_gate)
            {
                return m_audit.Count;
            }
        }
    }

    public IReadOnlyList<StationTxCommandAuditRecord> GetRecentAudit(
        int limit = 50)
    {
        if (limit is < 1 or > MaximumAuditRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (m_gate)
        {
            int skip = Math.Max(0, m_audit.Count - limit);
            return m_audit.Skip(skip).ToArray();
        }
    }

    public async Task<StationTxCommandBoundaryResult> ValidateAndExecuteAsync(
        StationTxCommandEnvelope envelope,
        StationTxCommandAuthority authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(authority);

        await m_executionGate.WaitAsync(cancellationToken);
        try
        {
            return await ValidateAndExecuteCoreAsync(
                envelope,
                authority,
                cancellationToken);
        }
        finally
        {
            m_executionGate.Release();
        }
    }

    private async Task<StationTxCommandBoundaryResult>
        ValidateAndExecuteCoreAsync(
            StationTxCommandEnvelope envelope,
            StationTxCommandAuthority authority,
            CancellationToken cancellationToken)
    {
        DateTimeOffset now = m_timeProvider.GetUtcNow();
        StationTxCommandCapabilities capabilities =
            GetCapabilities(envelope.ProtocolVersion);

        BoundaryFailure? failure = ValidateEnvelope(envelope, now);
        if (failure is null)
        {
            failure = ValidateSignature(envelope);
        }
        if (failure is null)
        {
            failure = ValidateAuthority(envelope, authority, now);
        }
        if (failure is null && !m_enabled)
        {
            failure = new(
                "boundary_disabled",
                "The station-local command boundary is disabled.");
        }
        if (failure is null && !m_adapter.IsRegistered)
        {
            failure = new(
                "adapter_unavailable",
                "No station-local command adapter is registered.");
        }
        if (failure is null && !m_adapter.ArmingAvailable)
        {
            failure = new(
                "arming_unavailable",
                "The station-local command adapter cannot be armed.");
        }
        if (failure is null && !m_adapter.SupportsSetTransmit)
        {
            failure = new(
                "command_unavailable",
                "The station-local command adapter does not support this command.");
        }
        if (failure is null)
        {
            lock (m_gate)
            {
                if (envelope.Sequence <= m_lastAcceptedSequence)
                {
                    failure = new(
                        "stale_sequence",
                        "The signed command sequence is stale or replayed.");
                }
                else
                {
                    m_lastAcceptedSequence = envelope.Sequence;
                }
            }
        }

        if (failure is not null)
        {
            return Reject(
                envelope,
                capabilities,
                failure.Code,
                failure.Message,
                now);
        }

        StationTxValidatedCommand command = new(
            envelope.CommandId,
            envelope.Sequence,
            envelope.StationId,
            envelope.RadioId,
            envelope.SessionId,
            envelope.BrowserClientId,
            envelope.LeaseId,
            envelope.GatewayInstanceId,
            envelope.EngineInstanceId,
            envelope.ClientHandle,
            envelope.Action,
            envelope.Enabled,
            envelope.IssuedAt,
            envelope.ExpiresAt);
        StationTxTransportResult transport;
        try
        {
            transport = await m_adapter.ExecuteAsync(
                command,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordAudit(envelope, "cancelled", "adapter-cancelled", now);
            throw;
        }
        catch (Exception)
        {
            RecordAudit(envelope, "faulted", "adapter-exception", now);
            throw;
        }
        if (!transport.Success)
        {
            string code = transport.OutcomeKnown
                ? "adapter_rejected"
                : "adapter_outcome_unknown";
            string message = string.IsNullOrWhiteSpace(transport.Message)
                ? "The station-local command adapter did not accept the command."
                : transport.Message;
            return Reject(envelope, capabilities, code, message, now);
        }

        StationTxCommandAuditRecord audit = RecordAudit(
            envelope,
            "accepted",
            "validated-and-adapter-accepted",
            now);
        return new StationTxCommandBoundaryResult(
            Success: true,
            Code: "accepted",
            Message: "The signed station-local command was accepted.",
            capabilities,
            audit);
    }

    internal static byte[] CreateSigningPayload(
        StationTxCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using MemoryStream stream = new();
        WriteString(stream, "AETHER-STATION-TX-COMMAND");
        WriteInt32(stream, envelope.ProtocolVersion);
        WriteString(stream, envelope.KeyId);
        WriteString(stream, envelope.CommandId);
        WriteInt64(stream, envelope.Sequence);
        WriteInt64(stream, envelope.IssuedAt.ToUnixTimeMilliseconds());
        WriteInt64(stream, envelope.ExpiresAt.ToUnixTimeMilliseconds());
        WriteString(stream, envelope.StationId);
        WriteString(stream, envelope.RadioId);
        WriteString(stream, envelope.SessionId);
        WriteString(stream, envelope.BrowserClientId);
        WriteString(stream, envelope.LeaseId);
        WriteString(stream, envelope.GatewayInstanceId);
        WriteString(stream, envelope.EngineInstanceId);
        WriteUInt32(stream, envelope.ClientHandle);
        WriteInt32(stream, (int)envelope.Action);
        stream.WriteByte(envelope.Enabled ? (byte)1 : (byte)0);
        return stream.ToArray();
    }

    private BoundaryFailure? ValidateEnvelope(
        StationTxCommandEnvelope envelope,
        DateTimeOffset now)
    {
        if (envelope.ProtocolVersion != ProtocolVersion)
        {
            return new(
                "unsupported_protocol_version",
                "The signed command protocol version is unsupported.");
        }
        if (!IsCanonicalToken(envelope.KeyId, MaximumKeyIdLength))
        {
            return new("invalid_key_id", "The signing key identifier is invalid.");
        }
        if (!Guid.TryParseExact(envelope.CommandId, "N", out Guid commandId) ||
            !string.Equals(
                commandId.ToString("N"),
                envelope.CommandId,
                StringComparison.Ordinal))
        {
            return new("invalid_command_id", "The command identifier is invalid.");
        }
        if (envelope.Sequence <= 0)
        {
            return new("invalid_sequence", "The command sequence must be positive.");
        }
        if (!IsCanonicalIdentifier(envelope.StationId) ||
            !IsCanonicalIdentifier(envelope.RadioId) ||
            !IsCanonicalIdentifier(envelope.SessionId) ||
            !IsCanonicalIdentifier(envelope.BrowserClientId) ||
            !IsCanonicalIdentifier(envelope.LeaseId) ||
            !IsCanonicalIdentifier(envelope.GatewayInstanceId) ||
            !IsCanonicalIdentifier(envelope.EngineInstanceId))
        {
            return new(
                "invalid_identity",
                "One or more signed command identities are invalid.");
        }
        if (envelope.ClientHandle == 0)
        {
            return new(
                "invalid_client_handle",
                "The protected FLEX client handle is required.");
        }
        if (envelope.Action != StationTxCommandAction.SetTransmit)
        {
            return new("invalid_action", "The signed command action is invalid.");
        }
        if (envelope.IssuedAt > now + MaximumClockSkew)
        {
            return new(
                "issued_in_future",
                "The signed command issue time is too far in the future.");
        }
        if (envelope.IssuedAt < now - MaximumEnvelopeAge)
        {
            return new("command_too_old", "The signed command is too old.");
        }
        if (envelope.ExpiresAt <= now)
        {
            return new("command_expired", "The signed command has expired.");
        }
        if (envelope.ExpiresAt <= envelope.IssuedAt ||
            envelope.ExpiresAt - envelope.IssuedAt > MaximumEnvelopeLifetime)
        {
            return new(
                "invalid_command_lifetime",
                "The signed command lifetime is invalid.");
        }
        if (!TryDecodeSignature(envelope.Signature, out _))
        {
            return new(
                "invalid_signature_encoding",
                "The signed command signature is malformed.");
        }

        return null;
    }

    private BoundaryFailure? ValidateSignature(StationTxCommandEnvelope envelope)
    {
        if (!m_verifier.IsAvailable)
        {
            return new(
                "signature_verifier_unavailable",
                "No station-local command signature verifier is available.");
        }
        if (!TryDecodeSignature(envelope.Signature, out byte[] signature))
        {
            return new(
                "invalid_signature_encoding",
                "The signed command signature is malformed.");
        }

        byte[] payload = CreateSigningPayload(envelope);
        if (!m_verifier.Verify(envelope.KeyId, payload, signature))
        {
            return new(
                "invalid_signature",
                "The signed command signature was not accepted.");
        }

        return null;
    }

    private BoundaryFailure? ValidateAuthority(
        StationTxCommandEnvelope envelope,
        StationTxCommandAuthority authority,
        DateTimeOffset now)
    {
        if (!string.Equals(m_stationId, envelope.StationId, StringComparison.Ordinal) ||
            !string.Equals(authority.StationId, envelope.StationId, StringComparison.Ordinal))
        {
            return new("station_mismatch", "The station identity does not match.");
        }
        if (!string.Equals(authority.RadioId, envelope.RadioId, StringComparison.Ordinal))
        {
            return new("radio_mismatch", "The radio identity does not match.");
        }
        if (!string.Equals(authority.SessionId, envelope.SessionId, StringComparison.Ordinal))
        {
            return new("session_mismatch", "The web session identity does not match.");
        }
        if (!string.Equals(
                authority.BrowserClientId,
                envelope.BrowserClientId,
                StringComparison.Ordinal))
        {
            return new(
                "browser_client_mismatch",
                "The browser client identity does not match.");
        }
        if (!string.Equals(authority.LeaseId, envelope.LeaseId, StringComparison.Ordinal) ||
            authority.LeaseExpiresAt <= now ||
            authority.LeaseExpiresAt < envelope.ExpiresAt)
        {
            return new("lease_mismatch", "The TX lease is absent, expired, or mismatched.");
        }
        if (!string.Equals(
                authority.GatewayInstanceId,
                envelope.GatewayInstanceId,
                StringComparison.Ordinal))
        {
            return new(
                "gateway_instance_mismatch",
                "The gateway instance identity does not match.");
        }
        if (!string.Equals(
                authority.EngineInstanceId,
                envelope.EngineInstanceId,
                StringComparison.Ordinal))
        {
            return new(
                "engine_instance_mismatch",
                "The station engine identity does not match.");
        }
        if (authority.ClientHandle == 0 ||
            authority.ClientHandle != envelope.ClientHandle)
        {
            return new(
                "client_handle_mismatch",
                "The protected FLEX client handle does not match.");
        }
        if (!authority.Authenticated)
        {
            return new("authentication_stale", "Authentication is not current.");
        }
        if (!authority.BrowserFresh ||
            !authority.EngineFresh ||
            !authority.GatewayFresh ||
            !authority.AuthorityFresh)
        {
            return new(
                "authority_stale",
                "The station command authority observations are stale.");
        }

        RadioTxOccupancySnapshot occupancy = authority.Occupancy;
        if (!string.Equals(
                occupancy.RadioId,
                envelope.RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            occupancy.ObservedAt is null ||
            occupancy.FreshUntil is null ||
            occupancy.FreshUntil <= now)
        {
            return new(
                "occupancy_stale",
                "Radio-authoritative TX occupancy is stale or mismatched.");
        }
        if (!occupancy.BrowserLeaseAllowed)
        {
            return new(
                "radio_not_idle",
                "Radio-authoritative TX occupancy is not idle.");
        }
        if (!occupancy.HasExclusiveLocalPttAuthority(envelope.ClientHandle))
        {
            return new(
                "local_ptt_authority_mismatch",
                "Exclusive Local PTT authority does not match the protected FLEX handle.");
        }

        StationTxSafetySnapshot safety = authority.Safety;
        if (safety.State != StationTxSafetyState.Armed ||
            !string.Equals(
                safety.RadioId,
                envelope.RadioId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                safety.EngineInstanceId,
                envelope.EngineInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(safety.LeaseId, envelope.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(
                safety.SessionId,
                envelope.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                safety.BrowserClientId,
                envelope.BrowserClientId,
                StringComparison.Ordinal) ||
            safety.ProtectedClientHandle != envelope.ClientHandle ||
            safety.HeartbeatDeadlineAt is null ||
            safety.HeartbeatDeadlineAt <= now)
        {
            return new(
                "safety_not_armed",
                "The independent TX safety identity is not freshly armed for this command.");
        }

        return null;
    }

    private StationTxCommandBoundaryResult Reject(
        StationTxCommandEnvelope envelope,
        StationTxCommandCapabilities capabilities,
        string code,
        string message,
        DateTimeOffset now)
    {
        StationTxCommandAuditRecord audit =
            RecordAudit(envelope, "rejected", code, now);
        return new StationTxCommandBoundaryResult(
            Success: false,
            code,
            message,
            capabilities,
            audit);
    }

    private StationTxCommandAuditRecord RecordAudit(
        StationTxCommandEnvelope envelope,
        string outcome,
        string reason,
        DateTimeOffset now)
    {
        StationTxCommandAuditRecord audit = new(
            SafeValue(envelope.CommandId),
            envelope.Sequence,
            SafeValue(envelope.KeyId),
            SafeValue(envelope.StationId),
            SafeValue(envelope.RadioId),
            SafeValue(envelope.SessionId),
            SafeValue(envelope.BrowserClientId),
            Fingerprint(envelope.LeaseId),
            SafeValue(envelope.EngineInstanceId),
            envelope.ClientHandle,
            envelope.Action,
            envelope.Enabled,
            outcome,
            reason,
            now);
        lock (m_gate)
        {
            m_audit.Add(audit);
            if (m_audit.Count > MaximumAuditRecords)
            {
                m_audit.RemoveRange(0, m_audit.Count - MaximumAuditRecords);
            }
        }
        return audit;
    }

    private string CapabilityReason()
    {
        if (!m_enabled)
        {
            return "boundary-disabled";
        }
        if (!m_verifier.IsAvailable)
        {
            return "signature-verifier-unavailable";
        }
        if (!m_adapter.IsRegistered)
        {
            return "adapter-unavailable";
        }
        if (!m_adapter.ArmingAvailable)
        {
            return "arming-unavailable";
        }
        if (!m_adapter.SupportsSetTransmit)
        {
            return "command-unavailable";
        }
        return "unavailable";
    }

    private static string RequireIdentifier(
        string value,
        string parameterName,
        int maximumLength)
    {
        if (!IsCanonicalToken(value, maximumLength))
        {
            throw new ArgumentException(
                "The station command identity is invalid.",
                parameterName);
        }
        return value;
    }

    private static bool IsCanonicalIdentifier(string value) =>
        IsCanonicalToken(value, MaximumIdentifierLength);

    private static bool IsCanonicalToken(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
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

    private static bool TryDecodeSignature(
        string value,
        out byte[] signature)
    {
        signature = [];
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            return false;
        }

        string padded = value.Replace('-', '+').Replace('_', '/');
        int remainder = padded.Length % 4;
        if (remainder == 1)
        {
            return false;
        }
        if (remainder > 0)
        {
            padded = padded.PadRight(padded.Length + 4 - remainder, '=');
        }

        try
        {
            signature = Convert.FromBase64String(padded);
            return signature.Length is > 0 and <= MaximumSignatureBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Fingerprint(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(digest.AsSpan(0, 8));
    }

    private static string SafeValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new(Math.Min(value.Length, MaximumIdentifierLength));
        foreach (char character in value)
        {
            if (builder.Length >= MaximumIdentifierLength)
            {
                break;
            }
            builder.Append(
                char.IsControl(character) || character is '\r' or '\n'
                    ? '_'
                    : character);
        }
        return builder.ToString();
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed record BoundaryFailure(string Code, string Message);
}

internal sealed class StationTxUnavailableCommandSignatureVerifier :
    IStationTxCommandSignatureVerifier
{
    public bool IsAvailable => false;

    public bool Verify(
        string keyId,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature) => false;
}

internal sealed class StationTxEcdsaCommandSignatureVerifier :
    IStationTxCommandSignatureVerifier,
    IDisposable
{
    private readonly string m_keyId;
    private readonly ECDsa m_key;
    private int m_disposed;

    public StationTxEcdsaCommandSignatureVerifier(
        string keyId,
        ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 64)
        {
            throw new ArgumentException(
                "The signing key identifier is invalid.",
                nameof(keyId));
        }
        if (subjectPublicKeyInfo.Length is < 32 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(subjectPublicKeyInfo));
        }

        m_keyId = keyId;
        m_key = ECDsa.Create();
        m_key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int bytesRead);
        if (bytesRead != subjectPublicKeyInfo.Length)
        {
            m_key.Dispose();
            throw new CryptographicException(
                "The station command public key contains trailing data.");
        }
    }

    public bool IsAvailable => Volatile.Read(ref m_disposed) == 0;

    public bool Verify(
        string keyId,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature)
    {
        if (!IsAvailable ||
            !string.Equals(keyId, m_keyId, StringComparison.Ordinal))
        {
            return false;
        }

        return m_key.VerifyData(
            payload,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) == 0)
        {
            m_key.Dispose();
        }
    }
}

internal sealed class StationTxUnavailableCommandAdapter :
    IStationTxCommandAdapter
{
    public bool IsRegistered => false;
    public bool ArmingAvailable => false;
    public bool SupportsSetTransmit => false;

    public Task<StationTxTransportResult> ExecuteAsync(
        StationTxValidatedCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(StationTxTransportResult.Rejected(
            "Production station command adapter is not registered."));
}
