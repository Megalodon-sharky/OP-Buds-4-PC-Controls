using OpBuds.Protocol.Framing;
using Shouldly;
using Xunit;

namespace OpBuds.Protocol.Tests;

public class VarintTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(306)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(1_000_000)]
    public void RoundTrips(int value)
    {
        Span<byte> buffer = stackalloc byte[Varint.MaxBytes];
        var written = Varint.Write(buffer, value);

        written.ShouldBe(Varint.MeasureBytes(value));
        Varint.TryRead(buffer[..written], out var read, out var consumed).ShouldBe(VarintStatus.Ok);
        read.ShouldBe(value);
        consumed.ShouldBe(written);
    }

    [Fact]
    public void DecodesTheRealBigFrameLength()
    {
        // 0x011F's reply carried b2 02 here. A byte-wise reader sees 0xB2 = 178 and
        // desynchronises permanently, which is exactly the published bug.
        Varint.TryRead([0xB2, 0x02], out var value, out var consumed).ShouldBe(VarintStatus.Ok);
        value.ShouldBe(306);
        consumed.ShouldBe(2);
    }

    [Fact]
    public void ShortLengthsStayOneByte() => Varint.MeasureBytes(7).ShouldBe(1);

    [Fact]
    public void TruncatedNeedsMoreData() =>
        Varint.TryRead([0xB2], out _, out _).ShouldBe(VarintStatus.NeedMoreData);

    [Fact]
    public void RunawayIsMalformed() =>
        Varint.TryRead([0x80, 0x80, 0x80, 0x80, 0x80], out _, out _).ShouldBe(VarintStatus.Malformed);
}

public class FrameCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(120)]
    [InlineData(121)]
    [InlineData(299)]
    [InlineData(1024)]
    public void RoundTrips(int payloadLength)
    {
        var payload = new byte[payloadLength];
        Random.Shared.NextBytes(payload);
        var frame = new OpFrame(0x1F, MessageTypes.Reply, 0x42, payload);

        var encoded = FrameCodec.Encode(frame);
        encoded.Length.ShouldBe(FrameCodec.MeasureEncoded(payloadLength));

        FrameCodec.TryDecode(encoded, out var decoded, out var consumed).ShouldBe(DecodeStatus.Ok);
        consumed.ShouldBe(encoded.Length);
        decoded.CommandId.ShouldBe(frame.CommandId);
        decoded.MessageType.ShouldBe(frame.MessageType);
        decoded.Sequence.ShouldBe(frame.Sequence);
        decoded.Payload.ToArray().ShouldBe(payload);
    }

    [Fact]
    public void BigFrameMatchesTheObservedWireBytes()
    {
        var frame = new OpFrame(0x1F, MessageTypes.Reply, 0x00, new byte[299]);
        var encoded = FrameCodec.Encode(frame);

        encoded.Length.ShouldBe(309);
        encoded[0].ShouldBe(FrameCodec.Magic);
        encoded[1].ShouldBe((byte)0xB2);
        encoded[2].ShouldBe((byte)0x02);

        // plen is little-endian 299 = 0x012B
        encoded[8].ShouldBe((byte)0x2B);
        encoded[9].ShouldBe((byte)0x01);
    }

    [Fact]
    public void CommandLabelCombinesTypeAndId()
    {
        var frame = new OpFrame(0x06, MessageTypes.Reply, 0x00, ReadOnlyMemory<byte>.Empty);
        frame.Command.ShouldBe((ushort)0x8106);
    }

    [Fact]
    public void HelloConsumesNineOfItsTenBytes()
    {
        // The trailing 0x12 sits outside the frame its own length field declares. Real
        // garbage on the wire, from the first exchange of every session.
        FrameCodec.TryDecode(Fixtures.Hello, out _, out var consumed).ShouldBe(DecodeStatus.Ok);
        consumed.ShouldBe(9);
    }

    [Fact]
    public void RejectsInconsistentPayloadLength()
    {
        // There is no checksum; this cross-check is the only structural validation available.
        var encoded = FrameCodec.Encode(new OpFrame(0x06, MessageTypes.Reply, 0, new byte[4]));
        encoded[8] = 0x09; // claim nine payload bytes where the length field says four

        FrameCodec.TryDecode(encoded, out _, out _).ShouldBe(DecodeStatus.Resync);
    }

    [Fact]
    public void NonMagicResyncs() =>
        FrameCodec.TryDecode([0x00, 0x01, 0x02], out _, out _).ShouldBe(DecodeStatus.Resync);

    [Fact]
    public void TruncatedNeedsMoreData()
    {
        var encoded = FrameCodec.Encode(new OpFrame(0x06, MessageTypes.Reply, 0, new byte[8]));
        FrameCodec.TryDecode(encoded.AsSpan(0, encoded.Length - 1), out _, out _)
            .ShouldBe(DecodeStatus.NeedMoreData);
    }

    [Fact]
    public void ImpossiblyShortLengthResyncs() =>
        FrameCodec.TryDecode([0xAA, 0x03, 0x00, 0x00], out _, out _).ShouldBe(DecodeStatus.Resync);
}

public class FrameReaderTests
{
    private static byte[] SampleStream()
    {
        var stream = new List<byte>();
        stream.AddRange(FrameCodec.Encode(new OpFrame(0x06, MessageTypes.Reply, 0x01, Fixtures.BatteryBothNinety)));
        stream.AddRange(FrameCodec.Encode(new OpFrame(0x0C, MessageTypes.Reply, 0x02, Fixtures.AncAdaptive)));
        stream.AddRange(FrameCodec.Encode(new OpFrame(0x1F, MessageTypes.Reply, 0x03, new byte[299])));
        stream.AddRange(FrameCodec.Encode(new OpFrame(0x04, MessageTypes.Notify, 0xFF, Fixtures.WearBothInEar)));
        return [.. stream];
    }

    private static List<OpFrame> ReadAll(byte[] stream, int chunkSize)
    {
        var reader = new FrameReader();
        var frames = new List<OpFrame>();

        for (var offset = 0; offset < stream.Length; offset += chunkSize)
        {
            reader.Append(stream.AsSpan(offset, Math.Min(chunkSize, stream.Length - offset)));
            frames.AddRange(reader.Drain());
        }

        return frames;
    }

    private static string Describe(IEnumerable<OpFrame> frames) =>
        string.Join(
            ",",
            frames.Select(f => $"{f.Command:X4}:{f.Sequence:X2}:{Convert.ToHexString(f.Payload.Span)}"));

    [Fact]
    public void ChunkBoundariesDoNotChangeTheFrameSequence()
    {
        // Chunk boundaries carry no protocol meaning, so every split must agree.
        var stream = SampleStream();
        var expected = Describe(ReadAll(stream, stream.Length));

        foreach (var chunkSize in new[] { 1, 2, 3, 7, 64, 255, 512 })
        {
            Describe(ReadAll(stream, chunkSize)).ShouldBe(expected, $"chunk size {chunkSize}");
        }
    }

    [Fact]
    public void SurvivesGarbageBetweenFrames()
    {
        var stream = SampleStream();
        var expected = Describe(ReadAll(stream, stream.Length));

        var withGarbage = new List<byte>();
        withGarbage.AddRange([0x00, 0xFF, 0x13]);
        withGarbage.AddRange(stream);

        var reader = new FrameReader();
        reader.Append(withGarbage.ToArray());
        var frames = reader.Drain();

        Describe(frames).ShouldBe(expected);
        reader.DiscardedBytes.ShouldBe(3);
    }

    [Fact]
    public void SurvivesTheHelloTrailer()
    {
        var reader = new FrameReader();
        reader.Append(Fixtures.Hello);
        reader.Append(FrameCodec.Encode(new OpFrame(0x06, MessageTypes.Reply, 0x01, Fixtures.BatteryBothNinety)));

        var frames = reader.Drain();

        frames.Count.ShouldBe(2);
        frames[1].Command.ShouldBe((ushort)0x8106);
        reader.DiscardedBytes.ShouldBe(1); // the stray 0x12
    }

    [Fact]
    public void TruncatedTailIsSilent()
    {
        var stream = SampleStream();
        var reader = new FrameReader();
        reader.Append(stream.AsSpan(0, stream.Length - 5));

        var frames = reader.Drain();

        frames.Count.ShouldBe(3);
        reader.Buffered.ShouldBeGreaterThan(0);

        reader.Append(stream.AsSpan(stream.Length - 5));
        reader.Drain().Count.ShouldBe(1);
    }

    [Fact]
    public void RunawayStreamThrowsRatherThanGrowing()
    {
        const int cap = FrameCodec.MaxPayloadLength;
        var reader = new FrameReader(maxBufferBytes: cap);

        // A valid header promising a payload that will never fully arrive: the frame needs
        // cap + 10 bytes, so the buffer hits its limit still one short of a complete frame.
        var header = FrameCodec.Encode(new OpFrame(0x1F, MessageTypes.Reply, 0, new byte[cap]));
        reader.Append(header.AsSpan(0, 20));
        reader.Append(new byte[cap - 20]);

        reader.Buffered.ShouldBe(cap);
        Should.Throw<FrameDesynchronizedException>(() => reader.Drain());
    }
}
