using System.Buffers.Binary;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class FlexVitaAudioDecoderTests
{
    [Fact]
    public void FloatStereoPacketDecodesBigEndianSamples()
    {
        const uint streamId = 0x04000001;
        FlexVitaAudioDecoder decoder = new();
        byte[] packet = CreateFloatPacket(
            streamId,
            [0.5f, -0.5f, 1f, -1f],
            hasTrailer: true);

        Assert.True(
            decoder.TryDecode(packet, streamId, out FlexAudioFrame? frame));
        Assert.NotNull(frame);
        Assert.Equal(streamId, frame.StreamId);
        Assert.InRange(frame.Samples[0], 16_382, 16_384);
        Assert.InRange(frame.Samples[1], -16_384, -16_382);
        Assert.Equal(short.MaxValue, frame.Samples[2]);
        Assert.Equal(-32_767, frame.Samples[3]);
    }

    [Fact]
    public void ReducedBandwidthPacketDuplicatesMonoToStereo()
    {
        const uint streamId = 0x04000002;
        FlexVitaAudioDecoder decoder = new();
        byte[] packet = CreateReducedPacket(streamId, [1234, -2345]);

        Assert.True(
            decoder.TryDecode(packet, streamId, out FlexAudioFrame? frame));
        Assert.NotNull(frame);
        Assert.Equal<short>([1234, 1234, -2345, -2345], frame.Samples);
    }

    [Fact]
    public void AudioFromAnotherStreamIsRejected()
    {
        FlexVitaAudioDecoder decoder = new();
        byte[] packet = CreateFloatPacket(
            0x04000003,
            [0f, 0f],
            hasTrailer: false);

        Assert.False(
            decoder.TryDecode(packet, 0x04000004, out FlexAudioFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public void BrowserAudioFrameUsesBoundedPcm16Layout()
    {
        byte[] frame = AudioFrameCodec.Encode(
            [100, -100, 200, -200],
            sequence: 9,
            sampleRate: 24_000);

        Assert.Equal("AETA", System.Text.Encoding.ASCII.GetString(frame, 0, 4));
        Assert.Equal(0, frame[4]);
        Assert.Equal(2, frame[5]);
        Assert.Equal(
            24_000,
            BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)));
        Assert.Equal(
            (uint)9,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal(
            (uint)2,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
        Assert.Equal(
            (short)-200,
            BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(22)));
    }

    private static byte[] CreateFloatPacket(
        uint streamId,
        float[] samples,
        bool hasTrailer)
    {
        byte[] payload = new byte[samples.Length * sizeof(float)];
        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt32BigEndian(
                payload.AsSpan(index * sizeof(float)),
                BitConverter.SingleToInt32Bits(samples[index]));
        }

        return CreatePacket(
            streamId,
            FlexVitaAudioDecoder.FloatStereoPacketClassCode,
            payload,
            hasTrailer);
    }

    private static byte[] CreateReducedPacket(
        uint streamId,
        short[] samples)
    {
        byte[] payload = new byte[samples.Length * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16BigEndian(
                payload.AsSpan(index * sizeof(short)),
                samples[index]);
        }

        return CreatePacket(
            streamId,
            FlexVitaAudioDecoder.ReducedBandwidthPacketClassCode,
            payload,
            hasTrailer: false);
    }

    private static byte[] CreatePacket(
        uint streamId,
        ushort packetClassCode,
        byte[] payload,
        bool hasTrailer)
    {
        const int headerBytes = 28;
        int byteCount =
            headerBytes + payload.Length + (hasTrailer ? sizeof(uint) : 0);
        int paddedBytes = (byteCount + 3) & ~3;
        byte[] packet = new byte[paddedBytes];
        uint word0 =
            0x30000000u |
            (hasTrailer ? 0x04000000u : 0) |
            (uint)(paddedBytes / 4);
        BinaryPrimitives.WriteUInt32BigEndian(packet, word0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), streamId);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(12),
            packetClassCode);
        payload.CopyTo(packet.AsSpan(headerBytes));
        return packet;
    }
}
