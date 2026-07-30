using System.Text.Json;

namespace AetherSDR.Web.Radio;

public static class RadioBrowserNetworkDiagnosticsParser
{
    private static readonly HashSet<string> AllowedProfiles =
        new(StringComparer.Ordinal)
        {
            "normal",
            "low"
        };
    private static readonly HashSet<string> AllowedAdaptation =
        new(StringComparer.Ordinal)
        {
            "automatic",
            "manual"
        };

    public static bool TryParse(
        JsonElement root,
        DateTimeOffset reportedAt,
        out RadioBrowserNetworkDiagnostics? diagnostics,
        out string? error)
    {
        diagnostics = null;
        error = null;

        if (!TryReadString(root, "profile", AllowedProfiles, out string profile) ||
            !TryReadString(
                root,
                "adaptation",
                AllowedAdaptation,
                out string adaptation) ||
            !TryReadBoolean(root, "pageVisible", out bool pageVisible) ||
            !TryReadDouble(
                root,
                "sampleMilliseconds",
                0,
                60_000,
                out double sampleMilliseconds) ||
            !TryReadInt64(
                root,
                "receivedBytes",
                1_000_000_000,
                out long receivedBytes) ||
            !TryReadInt64(
                root,
                "receivedMessages",
                1_000_000,
                out long receivedMessages) ||
            !TryReadDouble(
                root,
                "bytesPerSecond",
                0,
                1_000_000_000,
                out double bytesPerSecond) ||
            !TryReadDouble(
                root,
                "bitsPerSecond",
                0,
                8_000_000_000,
                out double bitsPerSecond) ||
            !TryReadDouble(
                root,
                "audioBytesPerSecond",
                0,
                1_000_000_000,
                out double audioBytesPerSecond) ||
            !TryReadDouble(
                root,
                "spectrumBytesPerSecond",
                0,
                1_000_000_000,
                out double spectrumBytesPerSecond) ||
            !TryReadDouble(
                root,
                "textBytesPerSecond",
                0,
                1_000_000_000,
                out double textBytesPerSecond) ||
            !TryReadDouble(
                root,
                "messagesPerSecond",
                0,
                1_000_000,
                out double messagesPerSecond) ||
            !TryReadDouble(
                root,
                "maximumGapMilliseconds",
                0,
                60_000,
                out double maximumGapMilliseconds) ||
            !TryReadInt64(
                root,
                "audioPackets",
                1_000_000,
                out long audioPackets) ||
            !TryReadInt64(
                root,
                "spectrumFrames",
                1_000_000,
                out long spectrumFrames) ||
            !TryReadInt64(
                root,
                "textMessages",
                1_000_000,
                out long textMessages) ||
            !TryReadInt64(
                root,
                "missingAudioPackets",
                long.MaxValue,
                out long missingAudioPackets))
        {
            error = "Browser network diagnostics contain invalid values.";
            return false;
        }

        double componentBytesPerSecond =
            audioBytesPerSecond +
            spectrumBytesPerSecond +
            textBytesPerSecond;
        double componentTolerance = Math.Max(1, bytesPerSecond * 0.01);
        if (componentBytesPerSecond > bytesPerSecond + componentTolerance)
        {
            error = "Browser network diagnostic rates are inconsistent.";
            return false;
        }

        diagnostics = new RadioBrowserNetworkDiagnostics(
            profile,
            adaptation,
            pageVisible,
            sampleMilliseconds,
            receivedBytes,
            receivedMessages,
            bytesPerSecond,
            bitsPerSecond,
            audioBytesPerSecond,
            spectrumBytesPerSecond,
            textBytesPerSecond,
            messagesPerSecond,
            maximumGapMilliseconds,
            audioPackets,
            spectrumFrames,
            textMessages,
            missingAudioPackets,
            reportedAt);
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

    private static bool TryReadInt64(
        JsonElement root,
        string name,
        long maximum,
        out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element) &&
            element.TryGetInt64(out value) &&
            value >= 0 &&
            value <= maximum;
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
}
