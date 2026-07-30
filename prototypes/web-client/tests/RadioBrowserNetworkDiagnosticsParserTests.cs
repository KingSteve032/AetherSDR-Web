using System.Text.Json;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class RadioBrowserNetworkDiagnosticsParserTests
{
    [Fact]
    public void ValidNetworkReportIsAcceptedAndServerTimestamped()
    {
        DateTimeOffset reportedAt =
            DateTimeOffset.Parse("2026-07-28T01:00:00Z");
        using JsonDocument document = JsonDocument.Parse(ValidReport);

        bool parsed = RadioBrowserNetworkDiagnosticsParser.TryParse(
            document.RootElement,
            reportedAt,
            out RadioBrowserNetworkDiagnostics? diagnostics,
            out string? error);

        Assert.True(parsed, error);
        Assert.NotNull(diagnostics);
        Assert.Equal("normal", diagnostics.Profile);
        Assert.Equal("automatic", diagnostics.Adaptation);
        Assert.Equal(128_000, diagnostics.BitsPerSecond);
        Assert.Equal(10_000, diagnostics.AudioBytesPerSecond);
        Assert.Equal(42, diagnostics.AudioPackets);
        Assert.Equal(reportedAt, diagnostics.ReportedAt);
    }

    [Theory]
    [InlineData("\"profile\": \"turbo\",")]
    [InlineData("\"sampleMilliseconds\": 90000,")]
    [InlineData("\"receivedBytes\": -1,")]
    [InlineData("\"maximumGapMilliseconds\": 90000,")]
    [InlineData("\"missingAudioPackets\": -1")]
    public void InvalidNetworkValuesAreRejected(string replacement)
    {
        string propertyName = replacement.Split(':', 2)[0];
        int propertyStart =
            ValidReport.IndexOf(propertyName, StringComparison.Ordinal);
        int lineEnd = ValidReport.IndexOf('\n', propertyStart);
        string invalidJson =
            ValidReport[..propertyStart] +
            replacement +
            (lineEnd >= 0 ? ValidReport[lineEnd..] : string.Empty);
        using JsonDocument document = JsonDocument.Parse(invalidJson);

        bool parsed = RadioBrowserNetworkDiagnosticsParser.TryParse(
            document.RootElement,
            DateTimeOffset.UtcNow,
            out RadioBrowserNetworkDiagnostics? diagnostics,
            out string? error);

        Assert.False(parsed);
        Assert.Null(diagnostics);
        Assert.NotNull(error);
    }

    [Fact]
    public void ComponentRateAboveTotalRateIsRejected()
    {
        string invalidJson = ValidReport.Replace(
            "\"spectrumBytesPerSecond\": 5000",
            "\"spectrumBytesPerSecond\": 9000",
            StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(invalidJson);

        bool parsed = RadioBrowserNetworkDiagnosticsParser.TryParse(
            document.RootElement,
            DateTimeOffset.UtcNow,
            out RadioBrowserNetworkDiagnostics? diagnostics,
            out string? error);

        Assert.False(parsed);
        Assert.Null(diagnostics);
        Assert.Equal(
            "Browser network diagnostic rates are inconsistent.",
            error);
    }

    private const string ValidReport =
        """
        {
          "cmd": "diagnostics.network",
          "profile": "normal",
          "adaptation": "automatic",
          "pageVisible": true,
          "sampleMilliseconds": 2000,
          "receivedBytes": 32000,
          "receivedMessages": 55,
          "bytesPerSecond": 16000,
          "bitsPerSecond": 128000,
          "audioBytesPerSecond": 10000,
          "spectrumBytesPerSecond": 5000,
          "textBytesPerSecond": 1000,
          "messagesPerSecond": 27.5,
          "maximumGapMilliseconds": 58,
          "audioPackets": 42,
          "spectrumFrames": 12,
          "textMessages": 1,
          "missingAudioPackets": 0
        }
        """;
}
