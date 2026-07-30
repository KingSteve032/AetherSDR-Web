using System.Buffers.Binary;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class FlexVitaFftDecoderTests
{
    [Fact]
    public void DbmRangeCanFollowRadioPanStatus()
    {
        FlexVitaFftDecoder decoder = new(-130, -40, 700);
        decoder.SetDbmRange(-140, -40);
        ushort[] pixels = Enumerable.Range(0, 64)
            .Select(index => (ushort)(index * 10))
            .ToArray();

        Assert.True(
            decoder.TryDecode(
                CreatePacket(0x40000004, 12, 0, 64, pixels),
                out FlexFftFrame? frame));
        Assert.NotNull(frame);
        Assert.Equal(-40, frame.Bins[0], precision: 3);
        Assert.InRange(frame.Bins[^1], -132, -129);
    }

    [Fact]
    public void CompleteFftPacketDecodesPixelValuesToDbm()
    {
        FlexVitaFftDecoder decoder = new(-130, -40, 700);
        ushort[] pixels = Enumerable.Range(0, 64)
            .Select(index => (ushort)(index * 10))
            .ToArray();
        byte[] packet = CreatePacket(
            streamId: 0x40000000,
            frameIndex: 7,
            startBin: 0,
            totalBins: 64,
            pixels);

        bool decoded = decoder.TryDecode(packet, out FlexFftFrame? frame);

        Assert.True(decoded);
        Assert.NotNull(frame);
        Assert.Equal((uint)0x40000000, frame.StreamId);
        Assert.Equal((uint)7, frame.FrameIndex);
        Assert.Equal(64, frame.Bins.Length);
        Assert.Equal(-40, frame.Bins[0], precision: 3);
        Assert.InRange(frame.Bins[^1], -122, -120);
    }

    [Fact]
    public void FragmentedFftFrameIsEmittedOnlyAfterAllBinsArrive()
    {
        FlexVitaFftDecoder decoder = new();
        ushort[] first = Enumerable.Repeat((ushort)350, 32).ToArray();
        ushort[] second = Enumerable.Repeat((ushort)400, 32).ToArray();

        bool firstDecoded = decoder.TryDecode(
            CreatePacket(0x40000001, 11, 0, 64, first),
            out FlexFftFrame? firstFrame);
        bool secondDecoded = decoder.TryDecode(
            CreatePacket(0x40000001, 11, 32, 64, second),
            out FlexFftFrame? secondFrame);

        Assert.False(firstDecoded);
        Assert.Null(firstFrame);
        Assert.True(secondDecoded);
        Assert.NotNull(secondFrame);
        Assert.Equal(64, secondFrame.Bins.Length);
    }

    [Fact]
    public void MalformedBinRangeIsRejected()
    {
        FlexVitaFftDecoder decoder = new();
        byte[] packet = CreatePacket(
            0x40000002,
            3,
            startBin: 60,
            totalBins: 64,
            Enumerable.Repeat((ushort)100, 8).ToArray());

        Assert.False(decoder.TryDecode(packet, out _));
    }

    [Fact]
    public void TallerObservedPixelSpaceDoesNotFlattenTheTrace()
    {
        FlexVitaFftDecoder decoder = new(-130, -40, 700);
        ushort[] pixels = Enumerable.Range(1000, 64)
            .Select(value => (ushort)value)
            .ToArray();

        Assert.True(
            decoder.TryDecode(
                CreatePacket(0x40000003, 9, 0, 64, pixels),
                out FlexFftFrame? frame));
        Assert.NotNull(frame);
        Assert.Equal(1064, frame.EffectiveYPixels);
        Assert.True(frame.Bins.Max() - frame.Bins.Min() > 4);
    }

    private static byte[] CreatePacket(
        uint streamId,
        uint frameIndex,
        ushort startBin,
        ushort totalBins,
        ushort[] pixels)
    {
        const int headerSize = 28;
        const int subheaderSize = 12;
        int byteCount = headerSize + subheaderSize + (pixels.Length * 2);
        int paddedByteCount = (byteCount + 3) & ~3;
        byte[] packet = new byte[paddedByteCount];

        uint word0 = 0x30000000u | (uint)(paddedByteCount / 4);
        BinaryPrimitives.WriteUInt32BigEndian(packet, word0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), streamId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), 0x00008003);
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(headerSize),
            startBin);
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(headerSize + 2),
            (ushort)pixels.Length);
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(headerSize + 4),
            2);
        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(headerSize + 6),
            totalBins);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(headerSize + 8),
            frameIndex);

        for (int index = 0; index < pixels.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                packet.AsSpan(
                    headerSize + subheaderSize + (index * 2)),
                pixels[index]);
        }

        return packet;
    }
}
