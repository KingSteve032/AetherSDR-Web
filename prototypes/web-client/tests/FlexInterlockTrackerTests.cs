using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class FlexInterlockTrackerTests
{
    [Fact]
    public void ReadyClearsPriorTransmitOwnerAndSource()
    {
        FlexInterlockTracker tracker = new();
        Assert.True(tracker.Observe(
            Fields(
                ("state", "TRANSMITTING"),
                ("tx_client_handle", "0x10"),
                ("source", "SW")),
            out _));

        Assert.True(tracker.Observe(
            Fields(("state", "READY")),
            out FlexInterlockObservation? ready));

        Assert.NotNull(ready);
        Assert.Equal("READY", ready.State);
        Assert.Null(ready.TxClientHandle);
        Assert.Equal(string.Empty, ready.Source);
    }

    [Fact]
    public void NewCycleWithoutOwnerDoesNotReuseCompletedCycleOwner()
    {
        FlexInterlockTracker tracker = new();
        tracker.Observe(
            Fields(
                ("state", "TRANSMITTING"),
                ("tx_client_handle", "0x10"),
                ("source", "SW")),
            out _);
        tracker.Observe(Fields(("state", "READY")), out _);

        Assert.True(tracker.Observe(
            Fields(("state", "PTT_REQUESTED")),
            out FlexInterlockObservation? requested));

        Assert.NotNull(requested);
        Assert.Equal("PTT_REQUESTED", requested.State);
        Assert.Null(requested.TxClientHandle);
        Assert.Equal(string.Empty, requested.Source);
    }

    [Fact]
    public void PartialUpdateWithinActiveCycleRetainsOwnerAndSource()
    {
        FlexInterlockTracker tracker = new();
        tracker.Observe(
            Fields(
                ("state", "PTT_REQUESTED"),
                ("tx_client_handle", "0x10"),
                ("source", "SW")),
            out _);

        Assert.True(tracker.Observe(
            Fields(("state", "TRANSMITTING")),
            out FlexInterlockObservation? transmitting));

        Assert.NotNull(transmitting);
        Assert.Equal("TRANSMITTING", transmitting.State);
        Assert.Equal((uint)0x10, transmitting.TxClientHandle);
        Assert.Equal("SW", transmitting.Source);
    }

    [Fact]
    public void InvalidOwnerUpdateIsRejectedWithoutDestroyingCurrentState()
    {
        FlexInterlockTracker tracker = new();
        tracker.Observe(
            Fields(
                ("state", "TRANSMITTING"),
                ("tx_client_handle", "0x10"),
                ("source", "SW")),
            out _);

        Assert.False(tracker.Observe(
            Fields(("tx_client_handle", "not-a-handle")),
            out FlexInterlockObservation? current));

        Assert.NotNull(current);
        Assert.Equal("TRANSMITTING", current.State);
        Assert.Equal((uint)0x10, current.TxClientHandle);
    }

    [Fact]
    public void ClearRemovesCachedInterlockState()
    {
        FlexInterlockTracker tracker = new();
        tracker.Observe(Fields(("state", "READY")), out _);

        tracker.Clear();

        Assert.Null(tracker.Current);
    }

    private static IReadOnlyDictionary<string, string> Fields(
        params (string Key, string Value)[] fields) =>
        fields.ToDictionary(
            field => field.Key,
            field => field.Value,
            StringComparer.OrdinalIgnoreCase);
}
