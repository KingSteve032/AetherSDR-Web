using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class RadioTuneTrackerTests
{
    [Fact]
    public void LatestTuneConfirmsOnlyFromMatchingRadioStatus()
    {
        RadioTuneTracker tracker = new();
        DateTimeOffset requestedAt =
            DateTimeOffset.Parse("2026-07-27T16:00:00Z");
        tracker.RecordRequest("B", 4, 14_074_000, requestedAt);

        tracker.Observe(
            [CreateSlice("B", 4, 14_100_000)],
            requestedAt.AddMilliseconds(20));

        Assert.Equal("pending", tracker.Snapshot.State);

        tracker.Observe(
            [CreateSlice("B", 4, 14_074_000)],
            requestedAt.AddMilliseconds(47));

        RadioTuneTimingDiagnostics result = tracker.Snapshot;
        Assert.Equal("confirmed", result.State);
        Assert.Equal(47, result.RadioRoundTripMilliseconds);
        Assert.Equal(requestedAt.AddMilliseconds(47), result.ConfirmedAt);
    }

    [Fact]
    public void NewTuneSupersedesAnOlderRadioEcho()
    {
        RadioTuneTracker tracker = new();
        DateTimeOffset requestedAt =
            DateTimeOffset.Parse("2026-07-27T16:00:00Z");
        tracker.RecordRequest("A", 2, 14_074_000, requestedAt);
        tracker.RecordRequest(
            "A",
            2,
            14_075_000,
            requestedAt.AddMilliseconds(10));

        tracker.Observe(
            [CreateSlice("A", 2, 14_074_000)],
            requestedAt.AddMilliseconds(25));

        Assert.Equal("pending", tracker.Snapshot.State);
        Assert.Equal(14_075_000, tracker.Snapshot.TargetFrequencyHz);
    }

    [Fact]
    public void FailureAppliesOnlyToTheCurrentTune()
    {
        RadioTuneTracker tracker = new();
        tracker.RecordRequest("A", 2, 14_074_000);
        tracker.RecordFailure(
            "B",
            3,
            7_074_000,
            "Unrelated failure");

        Assert.Equal("pending", tracker.Snapshot.State);

        tracker.RecordFailure(
            "A",
            2,
            14_074_000,
            "Radio rejected tune");

        Assert.Equal("failed", tracker.Snapshot.State);
        Assert.Equal("Radio rejected tune", tracker.Snapshot.Error);
    }

    private static SliceSnapshot CreateSlice(
        string id,
        int radioId,
        long frequencyHz) =>
        new(
            Id: id,
            FrequencyHz: frequencyHz,
            Mode: "USB",
            FilterLowHz: 300,
            FilterHighHz: 3_000,
            AfGain: 50,
            Squelch: 0,
            IsActive: true,
            IsTx: false,
            RadioId: radioId);
}
