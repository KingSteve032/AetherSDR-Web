using System.Text.Json;

namespace AetherSDR.Web.Radio;

public static class RadioBrowserAudioDiagnosticsParser
{
    private static readonly HashSet<string> AllowedContextStates =
        new(StringComparer.Ordinal)
        {
            "none",
            "suspended",
            "running",
            "closed",
            "interrupted"
        };
    private static readonly HashSet<string> AllowedDeliveryPaths =
        new(StringComparer.Ordinal)
        {
            "worker",
            "main-thread-fallback",
            "legacy-main-thread"
        };

    public static bool TryParse(
        JsonElement root,
        DateTimeOffset reportedAt,
        out RadioBrowserAudioDiagnostics? diagnostics,
        out string? error)
    {
        diagnostics = null;
        error = null;

        if (!TryReadBoolean(root, "enabled", out bool enabled) ||
            !TryReadString(
                root,
                "contextState",
                AllowedContextStates,
                out string contextState) ||
            !TryReadOptionalString(
                root,
                "deliveryPath",
                AllowedDeliveryPaths,
                "legacy-main-thread",
                out string deliveryPath) ||
            !TryReadOptionalBoolean(
                root,
                "pageVisible",
                true,
                out bool pageVisible) ||
            !TryReadOptionalBoolean(
                root,
                "playbackSuppressed",
                false,
                out bool playbackSuppressed) ||
            !TryReadOptionalBoolean(
                root,
                "recoveryPending",
                false,
                out bool recoveryPending) ||
            !TryReadOptionalInt64(
                root,
                "backgroundTransitions",
                out long backgroundTransitions) ||
            !TryReadOptionalInt64(
                root,
                "foregroundRecoveries",
                out long foregroundRecoveries) ||
            !TryReadBoolean(root, "sliceAvailable", out bool sliceAvailable) ||
            !TryReadSliceId(root, out string activeSliceId) ||
            !TryReadInt32(root, "sourceSampleRate", 0, 384_000, out int sourceRate) ||
            !TryReadInt32(root, "outputSampleRate", 0, 384_000, out int outputRate) ||
            !TryReadInt64(root, "receivedPackets", out long receivedPackets) ||
            !TryReadInt64(root, "receivedFrames", out long receivedFrames) ||
            !TryReadInt64(root, "malformedPackets", out long malformedPackets) ||
            !TryReadInt64(root, "missingPackets", out long missingPackets) ||
            !TryReadDouble(
                root,
                "maximumPacketGapMilliseconds",
                0,
                60_000,
                out double maximumPacketGap) ||
            !TryReadInt64(root, "playedFrames", out long playedFrames) ||
            !TryReadInt32(root, "queueFrames", 0, 480_000, out int queueFrames) ||
            !TryReadDouble(
                root,
                "queueMilliseconds",
                0,
                60_000,
                out double queueMilliseconds) ||
            !TryReadBoolean(root, "started", out bool started) ||
            !TryReadInt64(root, "underruns", out long underruns) ||
            !TryReadInt64(root, "trimmedFrames", out long trimmedFrames) ||
            !TryReadInt64(root, "clearedFrames", out long clearedFrames) ||
            !TryReadDouble(
                root,
                "baseLatencyMilliseconds",
                0,
                10_000,
                out double baseLatency) ||
            !TryReadDouble(
                root,
                "outputLatencyMilliseconds",
                0,
                10_000,
                out double outputLatency) ||
            !TryReadDouble(
                root,
                "estimatedLatencyMilliseconds",
                0,
                60_000,
                out double estimatedLatency) ||
            !TryReadOptionalDouble(
                root,
                "workletReportAgeMilliseconds",
                0,
                60_000,
                out double? workletReportAge))
        {
            error = "Browser audio diagnostics contain invalid values.";
            return false;
        }

        diagnostics = new RadioBrowserAudioDiagnostics(
            enabled,
            contextState,
            deliveryPath,
            pageVisible,
            playbackSuppressed,
            recoveryPending,
            backgroundTransitions,
            foregroundRecoveries,
            sliceAvailable,
            activeSliceId,
            sourceRate,
            outputRate,
            receivedPackets,
            receivedFrames,
            malformedPackets,
            missingPackets,
            maximumPacketGap,
            playedFrames,
            queueFrames,
            queueMilliseconds,
            started,
            underruns,
            trimmedFrames,
            clearedFrames,
            baseLatency,
            outputLatency,
            estimatedLatency,
            workletReportAge,
            reportedAt);
        return true;
    }

    private static bool TryReadBoolean(
        JsonElement root,
        string name,
        out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadOptionalBoolean(
        JsonElement root,
        string name,
        bool defaultValue,
        out bool value)
    {
        value = defaultValue;
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return true;
        }
        if (element.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadString(
        JsonElement root,
        string name,
        IReadOnlySet<string> allowed,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return allowed.Contains(value);
    }

    private static bool TryReadOptionalString(
        JsonElement root,
        string name,
        IReadOnlySet<string> allowed,
        string defaultValue,
        out string value)
    {
        value = defaultValue;
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return true;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return allowed.Contains(value);
    }

    private static bool TryReadSliceId(
        JsonElement root,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(
                "activeSliceId",
                out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return value.Length <= 16 &&
            value.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_');
    }

    private static bool TryReadInt32(
        JsonElement root,
        string name,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element) &&
            element.TryGetInt32(out value) &&
            value >= minimum &&
            value <= maximum;
    }

    private static bool TryReadInt64(
        JsonElement root,
        string name,
        out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element) &&
            element.TryGetInt64(out value) &&
            value >= 0;
    }

    private static bool TryReadOptionalInt64(
        JsonElement root,
        string name,
        out long value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return true;
        }

        return element.TryGetInt64(out value) && value >= 0;
    }

    private static bool TryReadDouble(
        JsonElement root,
        string name,
        double minimum,
        double maximum,
        out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element) &&
            element.TryGetDouble(out value) &&
            double.IsFinite(value) &&
            value >= minimum &&
            value <= maximum;
    }

    private static bool TryReadOptionalDouble(
        JsonElement root,
        string name,
        double minimum,
        double maximum,
        out double? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!element.TryGetDouble(out double parsed) ||
            !double.IsFinite(parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
