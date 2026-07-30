using System.Text.Json;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class RadioBrowserAudioDiagnosticsParserTests
{
    [Fact]
    public void ValidBrowserAudioReportIsAcceptedAndServerTimestamped()
    {
        DateTimeOffset reportedAt =
            DateTimeOffset.Parse("2026-07-27T22:00:00Z");
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "cmd": "diagnostics.audio",
              "enabled": true,
              "contextState": "running",
              "deliveryPath": "worker",
              "pageVisible": true,
              "playbackSuppressed": false,
              "recoveryPending": false,
              "backgroundTransitions": 3,
              "foregroundRecoveries": 3,
              "sliceAvailable": true,
              "activeSliceId": "B",
              "sourceSampleRate": 24000,
              "outputSampleRate": 48000,
              "receivedPackets": 100,
              "receivedFrames": 30720,
              "malformedPackets": 0,
              "missingPackets": 2,
              "maximumPacketGapMilliseconds": 38.5,
              "playedFrames": 30000,
              "queueFrames": 480,
              "queueMilliseconds": 20,
              "started": true,
              "underruns": 1,
              "trimmedFrames": 64,
              "clearedFrames": 480,
              "baseLatencyMilliseconds": 5,
              "outputLatencyMilliseconds": 12,
              "estimatedLatencyMilliseconds": 37,
              "workletReportAgeMilliseconds": 250
            }
            """);

        bool parsed = RadioBrowserAudioDiagnosticsParser.TryParse(
            document.RootElement,
            reportedAt,
            out RadioBrowserAudioDiagnostics? diagnostics,
            out string? error);

        Assert.True(parsed, error);
        Assert.NotNull(diagnostics);
        Assert.Equal("B", diagnostics.ActiveSliceId);
        Assert.Equal("worker", diagnostics.DeliveryPath);
        Assert.True(diagnostics.PageVisible);
        Assert.False(diagnostics.PlaybackSuppressed);
        Assert.Equal(3, diagnostics.BackgroundTransitions);
        Assert.Equal(3, diagnostics.ForegroundRecoveries);
        Assert.Equal(480, diagnostics.QueueFrames);
        Assert.Equal(2, diagnostics.MissingPackets);
        Assert.Equal(38.5, diagnostics.MaximumPacketGapMilliseconds);
        Assert.Equal(37, diagnostics.EstimatedLatencyMilliseconds);
        Assert.Equal(reportedAt, diagnostics.ReportedAt);
    }

    [Theory]
    [InlineData("\"queueFrames\": -1,")]
    [InlineData("\"contextState\": \"invented\",")]
    [InlineData("\"activeSliceId\": \"A<script>\",")]
    [InlineData("\"estimatedLatencyMilliseconds\": 90000,")]
    [InlineData("\"backgroundTransitions\": -1,")]
    public void InvalidBrowserAudioValuesAreRejected(string replacement)
    {
        string json =
            """
            {
              "enabled": true,
              "contextState": "running",
              "deliveryPath": "worker",
              "pageVisible": true,
              "playbackSuppressed": false,
              "recoveryPending": false,
              "backgroundTransitions": 0,
              "foregroundRecoveries": 0,
              "sliceAvailable": true,
              "activeSliceId": "A",
              "sourceSampleRate": 24000,
              "outputSampleRate": 48000,
              "receivedPackets": 1,
              "receivedFrames": 256,
              "malformedPackets": 0,
              "missingPackets": 0,
              "maximumPacketGapMilliseconds": 12,
              "playedFrames": 128,
              "queueFrames": 128,
              "queueMilliseconds": 5.3,
              "started": true,
              "underruns": 0,
              "trimmedFrames": 0,
              "clearedFrames": 0,
              "baseLatencyMilliseconds": 5,
              "outputLatencyMilliseconds": 10,
              "estimatedLatencyMilliseconds": 20.3,
              "workletReportAgeMilliseconds": null
            }
            """;
        string propertyName = replacement.Split(':', 2)[0];
        int propertyStart = json.IndexOf(propertyName, StringComparison.Ordinal);
        int lineEnd = json.IndexOf('\n', propertyStart);
        string invalidJson =
            json[..propertyStart] +
            replacement +
            (lineEnd >= 0 ? json[lineEnd..] : string.Empty);
        using JsonDocument document = JsonDocument.Parse(invalidJson);

        bool parsed = RadioBrowserAudioDiagnosticsParser.TryParse(
            document.RootElement,
            DateTimeOffset.UtcNow,
            out RadioBrowserAudioDiagnostics? diagnostics,
            out string? error);

        Assert.False(parsed);
        Assert.Null(diagnostics);
        Assert.NotNull(error);
    }
}
