using System.Text.Json;

namespace AetherSDR.Web.Radio;

internal enum BrowserTxRequestKind
{
    Acquire,
    Renew,
    Release,
    Intent,
    Heartbeat
}

internal enum BrowserTxIntentKind
{
    Mox,
    Ptt,
    Tune,
    Microphone,
    Cw
}

internal sealed record BrowserTxIntent(
    string IntentId,
    BrowserTxIntentKind Kind,
    string Action,
    bool? Enabled,
    string? Text);

internal sealed record BrowserTxRequest(
    long RequestId,
    BrowserTxRequestKind Kind,
    long Sequence,
    int? Seconds,
    string? LeaseId,
    BrowserTxIntent? Intent);

internal sealed record BrowserTxIntentResult(
    bool Ok,
    bool Validated,
    string Outcome,
    string? Error,
    long Sequence,
    string IntentId,
    string Action,
    DateTimeOffset ObservedAt,
    BrowserTxCapability Capability);

internal sealed record BrowserTxHeartbeatResult(
    bool Ok,
    string Outcome,
    string? Error,
    long Sequence,
    DateTimeOffset ObservedAt,
    BrowserTxCapability Capability);

internal static class RadioBrowserTxProtocol
{
    public const int Version = 2;
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    public const int MaximumIntentIdLength = 64;
    public const int MaximumCwTextLength = 32;

    private static readonly HashSet<string> AcquireProperties =
        new(StringComparer.Ordinal)
        {
            "id",
            "cmd",
            "protocolVersion",
            "sequence",
            "seconds"
        };

    private static readonly HashSet<string> LeaseProperties =
        new(StringComparer.Ordinal)
        {
            "id",
            "cmd",
            "protocolVersion",
            "sequence",
            "seconds",
            "leaseId"
        };

    private static readonly HashSet<string> ReleaseProperties =
        new(StringComparer.Ordinal)
        {
            "id",
            "cmd",
            "protocolVersion",
            "sequence",
            "leaseId"
        };

    private static readonly HashSet<string> HeartbeatProperties =
        new(StringComparer.Ordinal)
        {
            "id",
            "cmd",
            "protocolVersion",
            "sequence",
            "leaseId"
        };

    private static readonly HashSet<string> IntentProperties =
        new(StringComparer.Ordinal)
        {
            "id",
            "cmd",
            "protocolVersion",
            "sequence",
            "leaseId",
            "intentId",
            "action",
            "values"
        };

    private static readonly HashSet<string> EnabledProperties =
        new(StringComparer.Ordinal)
        {
            "enabled"
        };

    private static readonly HashSet<string> CwProperties =
        new(StringComparer.Ordinal)
        {
            "text"
        };

    public static bool TryParse(
        JsonElement root,
        out BrowserTxRequest? request,
        out string error)
    {
        request = null;
        error = string.Empty;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryReadPositiveInt64(root, "id", out long requestId) ||
            !TryReadString(root, "cmd", out string command) ||
            !TryReadInt32(root, "protocolVersion", out int version) ||
            version != Version ||
            !TryReadPositiveInt64(root, "sequence", out long sequence))
        {
            error = "invalid-tx-envelope";
            return false;
        }

        return command switch
        {
            "tx.acquire" => TryParseAcquire(
                root,
                requestId,
                sequence,
                out request,
                out error),
            "tx.renew" => TryParseRenew(
                root,
                requestId,
                sequence,
                out request,
                out error),
            "tx.release" => TryParseRelease(
                root,
                requestId,
                sequence,
                out request,
                out error),
            "tx.intent" => TryParseIntent(
                root,
                requestId,
                sequence,
                out request,
                out error),
            "tx.heartbeat" => TryParseHeartbeat(
                root,
                requestId,
                sequence,
                out request,
                out error),
            _ => Fail("unknown-tx-command", out request, out error)
        };
    }

    private static bool TryParseAcquire(
        JsonElement root,
        long requestId,
        long sequence,
        out BrowserTxRequest? request,
        out string error)
    {
        if (!HasOnlyUniqueProperties(root, AcquireProperties) ||
            !TryReadLeaseSeconds(root, out int seconds))
        {
            return Fail("invalid-tx-acquire", out request, out error);
        }

        request = new BrowserTxRequest(
            requestId,
            BrowserTxRequestKind.Acquire,
            sequence,
            seconds,
            LeaseId: null,
            Intent: null);
        error = string.Empty;
        return true;
    }

    private static bool TryParseRenew(
        JsonElement root,
        long requestId,
        long sequence,
        out BrowserTxRequest? request,
        out string error)
    {
        if (!HasOnlyUniqueProperties(root, LeaseProperties) ||
            !TryReadLeaseSeconds(root, out int seconds) ||
            !TryReadLeaseId(root, out string leaseId))
        {
            return Fail("invalid-tx-renew", out request, out error);
        }

        request = new BrowserTxRequest(
            requestId,
            BrowserTxRequestKind.Renew,
            sequence,
            seconds,
            leaseId,
            Intent: null);
        error = string.Empty;
        return true;
    }

    private static bool TryParseRelease(
        JsonElement root,
        long requestId,
        long sequence,
        out BrowserTxRequest? request,
        out string error)
    {
        if (!HasOnlyUniqueProperties(root, ReleaseProperties) ||
            !TryReadLeaseId(root, out string leaseId))
        {
            return Fail("invalid-tx-release", out request, out error);
        }

        request = new BrowserTxRequest(
            requestId,
            BrowserTxRequestKind.Release,
            sequence,
            Seconds: null,
            leaseId,
            Intent: null);
        error = string.Empty;
        return true;
    }

    private static bool TryParseHeartbeat(
        JsonElement root,
        long requestId,
        long sequence,
        out BrowserTxRequest? request,
        out string error)
    {
        if (!HasOnlyUniqueProperties(root, HeartbeatProperties) ||
            !TryReadLeaseId(root, out string leaseId))
        {
            return Fail("invalid-tx-heartbeat", out request, out error);
        }

        request = new BrowserTxRequest(
            requestId,
            BrowserTxRequestKind.Heartbeat,
            sequence,
            Seconds: null,
            leaseId,
            Intent: null);
        error = string.Empty;
        return true;
    }

    private static bool TryParseIntent(
        JsonElement root,
        long requestId,
        long sequence,
        out BrowserTxRequest? request,
        out string error)
    {
        if (!HasOnlyUniqueProperties(root, IntentProperties) ||
            !TryReadLeaseId(root, out string leaseId) ||
            !TryReadIdentifier(
                root,
                "intentId",
                MaximumIntentIdLength,
                out string intentId) ||
            !TryReadString(root, "action", out string action) ||
            !root.TryGetProperty("values", out JsonElement values) ||
            values.ValueKind != JsonValueKind.Object ||
            !TryParseIntentPayload(
                action,
                values,
                intentId,
                out BrowserTxIntent? intent))
        {
            return Fail("invalid-tx-intent", out request, out error);
        }

        request = new BrowserTxRequest(
            requestId,
            BrowserTxRequestKind.Intent,
            sequence,
            Seconds: null,
            leaseId,
            intent);
        error = string.Empty;
        return true;
    }

    private static bool TryParseIntentPayload(
        string action,
        JsonElement values,
        string intentId,
        out BrowserTxIntent? intent)
    {
        intent = null;
        BrowserTxIntentKind kind;
        bool? enabled = null;
        string? text = null;

        switch (action)
        {
            case "mox.set":
                kind = BrowserTxIntentKind.Mox;
                if (!TryReadEnabled(values, out enabled))
                {
                    return false;
                }
                break;
            case "ptt.set":
                kind = BrowserTxIntentKind.Ptt;
                if (!TryReadEnabled(values, out enabled))
                {
                    return false;
                }
                break;
            case "tune.set":
                kind = BrowserTxIntentKind.Tune;
                if (!TryReadEnabled(values, out enabled))
                {
                    return false;
                }
                break;
            case "microphone.set":
                kind = BrowserTxIntentKind.Microphone;
                if (!TryReadEnabled(values, out enabled))
                {
                    return false;
                }
                break;
            case "cw.send":
                kind = BrowserTxIntentKind.Cw;
                if (!HasOnlyUniqueProperties(values, CwProperties) ||
                    !TryReadString(values, "text", out string rawText))
                {
                    return false;
                }
                text = rawText.Trim();
                if (text.Length is 0 or > MaximumCwTextLength ||
                    text.Any(character =>
                        character is < ' ' or > '~' ||
                        character is '\\' or '"'))
                {
                    return false;
                }
                break;
            default:
                return false;
        }

        intent = new BrowserTxIntent(
            intentId,
            kind,
            action,
            enabled,
            text);
        return true;
    }

    private static bool TryReadEnabled(
        JsonElement values,
        out bool? enabled)
    {
        enabled = null;
        if (!HasOnlyUniqueProperties(values, EnabledProperties) ||
            !values.TryGetProperty("enabled", out JsonElement property) ||
            property.ValueKind is not
                JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }
        enabled = property.GetBoolean();
        return true;
    }

    private static bool TryReadLeaseSeconds(
        JsonElement root,
        out int seconds)
    {
        seconds = 0;
        return TryReadInt32(root, "seconds", out seconds) &&
            seconds >= TxLeaseManager.MinimumLeaseDuration.TotalSeconds &&
            seconds <= TxLeaseManager.MaximumLeaseDuration.TotalSeconds;
    }

    private static bool TryReadLeaseId(
        JsonElement root,
        out string leaseId)
    {
        leaseId = string.Empty;
        if (!TryReadString(root, "leaseId", out string raw))
        {
            return false;
        }
        string normalized = raw.Trim();
        if (normalized.Length != 32 ||
            normalized.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            return false;
        }
        leaseId = normalized;
        return true;
    }

    private static bool TryReadIdentifier(
        JsonElement root,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!TryReadString(root, propertyName, out string raw))
        {
            return false;
        }
        string normalized = raw.Trim();
        if (normalized.Length is 0 ||
            normalized.Length > maximumLength ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.' and not ':'))
        {
            return false;
        }
        value = normalized;
        return true;
    }

    private static bool TryReadString(
        JsonElement root,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadInt32(
        JsonElement root,
        string propertyName,
        out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static bool TryReadPositiveInt64(
        JsonElement root,
        string propertyName,
        out long value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value) &&
            value > 0 &&
            value <= MaximumSafeInteger;
    }

    private static bool HasOnlyUniqueProperties(
        JsonElement element,
        IReadOnlySet<string> allowed)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Fail(
        string errorValue,
        out BrowserTxRequest? request,
        out string error)
    {
        request = null;
        error = errorValue;
        return false;
    }
}
