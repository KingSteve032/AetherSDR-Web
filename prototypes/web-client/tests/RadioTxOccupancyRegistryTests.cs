using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class RadioTxOccupancyRegistryTests
{
    [Fact]
    public void MissingInterlockObservationIsUnknown()
    {
        RadioTxOccupancyRegistry registry = new();

        RadioTxOccupancySnapshot snapshot = registry.GetSnapshot("radio-a");

        Assert.Equal(RadioTxOccupancyState.Unknown, snapshot.State);
        Assert.False(snapshot.BrowserLeaseAllowed);
        Assert.Empty(snapshot.Occupants);
        Assert.Empty(snapshot.LocalPttOwners);
    }

    [Fact]
    public void LocalPttAssignmentDoesNotImplyTheRadioIsTransmitting()
    {
        RadioTxOccupancyRegistry registry = new();

        RadioTxOccupancySnapshot snapshot = registry.ObserveInterlock(
            "radio-a",
            "session-a",
            0x10,
            "READY",
            0x10,
            "SW",
            [Client(0x10, "AetherSDR", "Web", localPtt: true, ours: true)]);

        Assert.Equal(RadioTxOccupancyState.Idle, snapshot.State);
        Assert.True(snapshot.BrowserLeaseAllowed);
        Assert.Empty(snapshot.Occupants);
        RadioTxOccupant pttOwner = Assert.Single(snapshot.LocalPttOwners);
        Assert.True(pttOwner.AetherOwned);
        Assert.Equal((uint)0x10, pttOwner.ClientHandle);
        Assert.True(snapshot.HasExclusiveLocalPttAuthority(0x10));
    }

    [Fact]
    public void TransmittingWithExactReporterHandleIsAetherOwned()
    {
        RadioTxOccupancyRegistry registry = new();

        RadioTxOccupancySnapshot snapshot = registry.ObserveInterlock(
            "radio-a",
            "session-a",
            0x10,
            "TRANSMITTING",
            0x10,
            "SW",
            [Client(0x10, "AetherSDR", "Web", localPtt: true, ours: true)]);

        Assert.Equal(RadioTxOccupancyState.AetherOwned, snapshot.State);
        Assert.False(snapshot.BrowserLeaseAllowed);
        RadioTxOccupant occupant = Assert.Single(snapshot.Occupants);
        Assert.True(occupant.AetherOwned);
        Assert.Equal((uint)0x10, occupant.ClientHandle);
    }

    [Fact]
    public void SmartSdrTransmitHandleIsExternal()
    {
        RadioTxOccupancyRegistry registry = new();

        RadioTxOccupancySnapshot snapshot = registry.ObserveInterlock(
            "radio-a",
            "session-a",
            0x10,
            "TRANSMITTING",
            0x20,
            "SW",
            [
                Client(0x10, "AetherSDR", "Web", localPtt: false, ours: true),
                Client(0x20, "SmartSDR", "Club PC", localPtt: true, ours: false)
            ]);

        Assert.Equal(RadioTxOccupancyState.External, snapshot.State);
        RadioTxOccupant occupant = Assert.Single(snapshot.Occupants);
        Assert.False(occupant.AetherOwned);
        Assert.Equal("SmartSDR", occupant.Program);
        Assert.Equal("Club PC", occupant.Station);
        RadioTxOccupant pttOwner = Assert.Single(snapshot.LocalPttOwners);
        Assert.False(pttOwner.AetherOwned);
        Assert.Equal((uint)0x20, pttOwner.ClientHandle);
        Assert.False(snapshot.HasExclusiveLocalPttAuthority(0x10));
    }

    [Theory]
    [InlineData("MIC")]
    [InlineData("ACC")]
    [InlineData("RCA")]
    public void OwnerlessHardwarePttIsExternal(string source)
    {
        RadioTxOccupancyRegistry registry = new();

        RadioTxOccupancySnapshot snapshot = registry.ObserveInterlock(
            "radio-a",
            "session-a",
            0x10,
            "TRANSMITTING",
            null,
            source,
            [Client(0x10, "AetherSDR", "Web", localPtt: true, ours: true)]);

        Assert.Equal(RadioTxOccupancyState.External, snapshot.State);
        RadioTxOccupant occupant = Assert.Single(snapshot.Occupants);
        Assert.Equal((uint)0, occupant.ClientHandle);
        Assert.Equal(source, occupant.Source);
    }

    [Fact]
    public void OwnerlessSoftwareTransmitIsAmbiguous()
    {
        RadioTxOccupancyRegistry registry = new();

        RadioTxOccupancySnapshot snapshot = registry.ObserveInterlock(
            "radio-a",
            "session-a",
            0x10,
            "TRANSMITTING",
            null,
            "SW",
            [Client(0x10, "AetherSDR", "Web", localPtt: true, ours: true)]);

        Assert.Equal(RadioTxOccupancyState.Ambiguous, snapshot.State);
        Assert.False(snapshot.BrowserLeaseAllowed);
    }

    [Fact]
    public void ReporterDisagreementFailsClosedAsAmbiguous()
    {
        RadioTxOccupancyRegistry registry = new();
        registry.ObserveInterlock(
            "radio-a",
            "session-a",
            0x10,
            "READY",
            0x10,
            "SW",
            [Client(0x10, "AetherSDR", "Web A", localPtt: true, ours: true)]);

        RadioTxOccupancySnapshot snapshot = registry.ObserveInterlock(
            "radio-a",
            "session-b",
            0x11,
            "TRANSMITTING",
            0x20,
            "SW",
            [
                Client(0x11, "AetherSDR", "Web B", localPtt: false, ours: true),
                Client(0x20, "SmartSDR", "Club PC", localPtt: true, ours: false)
            ]);

        Assert.Equal(RadioTxOccupancyState.Ambiguous, snapshot.State);
        Assert.False(snapshot.BrowserLeaseAllowed);
    }

    [Fact]
    public void StaleInterlockObservationBecomesUnknown()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));
        RadioTxOccupancyRegistry registry = new(time);
        registry.ObserveInterlock(
            "radio-a",
            "session-a",
            0x10,
            "READY",
            0x10,
            "SW",
            [Client(0x10, "AetherSDR", "Web", localPtt: true, ours: true)]);

        time.Advance(RadioTxOccupancyRegistry.ObservationLifetime);
        RadioTxOccupancySnapshot snapshot = registry.GetSnapshot("radio-a");

        Assert.Equal(RadioTxOccupancyState.Unknown, snapshot.State);
        Assert.False(snapshot.BrowserLeaseAllowed);
        Assert.Empty(snapshot.LocalPttOwners);
    }

    [Fact]
    public void RemovingOneReporterPreservesAnotherFreshExternalObservation()
    {
        RadioTxOccupancyRegistry registry = new();
        registry.ObserveInterlock(
            "radio-a",
            "session-a",
            0x10,
            "TRANSMITTING",
            0x20,
            "SW",
            [Client(0x20, "SmartSDR", "Club PC", localPtt: true, ours: false)]);
        registry.ObserveInterlock(
            "radio-a",
            "session-b",
            0x11,
            "TRANSMITTING",
            0x20,
            "SW",
            [Client(0x20, "SmartSDR", "Club PC", localPtt: true, ours: false)]);

        RadioTxOccupancySnapshot snapshot = registry.RemoveReporter(
            "radio-a",
            "session-a");

        Assert.Equal(RadioTxOccupancyState.External, snapshot.State);
        Assert.Equal((uint)0x20, Assert.Single(snapshot.Occupants).ClientHandle);
    }

    private static RadioGuiClientDiagnostics Client(
        uint handle,
        string program,
        string station,
        bool localPtt,
        bool ours) =>
        new(
            handle,
            Guid.NewGuid().ToString(),
            program,
            station,
            "10.2.0.25",
            localPtt,
            ours);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration)
        {
            m_now = m_now.Add(duration);
        }
    }
}
