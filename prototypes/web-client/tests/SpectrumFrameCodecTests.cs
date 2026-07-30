using System.Buffers.Binary;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class SpectrumFrameCodecTests
{
    [Fact]
    public void VersionThreeFrameCarriesItsPanFrequencyFrame()
    {
        float[] bins = Enumerable.Repeat(-100f, 64).ToArray();

        byte[] frame = SpectrumFrameCodec.Encode(
            bins,
            sequence: 7,
            centerFrequencyHz: 7_074_000,
            bandwidthHz: 200_000,
            streamId: 0x40000001);

        Assert.Equal(28 + (64 * sizeof(short)), frame.Length);
        Assert.Equal(3, frame[5]);
        Assert.Equal(
            0x40000001u,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20, 4)));
        Assert.Equal(
            7_074_000,
            BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(12, 8)));
        Assert.Equal(
            200_000,
            BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(24, 4)));
    }
}
