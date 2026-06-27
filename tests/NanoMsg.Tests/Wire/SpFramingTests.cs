// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using NanoMsg.Wire;

namespace NanoMsg.Tests.Wire;

public sealed class SpFramingTests
{
    [Test]
    public async Task Writes_then_reads_single_frame()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        ArrayBufferWriter<byte> writer = new();
        SpFraming.WriteFrame(writer, payload);

        ReadOnlySequence<byte> buffer = new(writer.WrittenMemory);
        bool read = SpFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> body);

        await Assert.That(read).IsTrue();
        await Assert.That(body.ToArray().SequenceEqual(payload)).IsTrue();
        await Assert.That(buffer.Length).IsEqualTo(0L);
    }

    [Test]
    public async Task Reads_multiple_frames_including_empty_body()
    {
        ArrayBufferWriter<byte> writer = new();
        SpFraming.WriteFrame(writer, new byte[] { 10 });
        SpFraming.WriteFrame(writer, new byte[] { 20, 21 });
        SpFraming.WriteFrame(writer, ReadOnlySpan<byte>.Empty);

        ReadOnlySequence<byte> buffer = new(writer.WrittenMemory);

        bool first = SpFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> a);
        bool second = SpFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> b);
        bool third = SpFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> c);
        bool fourth = SpFraming.TryReadFrame(ref buffer, out _);

        await Assert.That(first).IsTrue();
        await Assert.That(a.ToArray().SequenceEqual(new byte[] { 10 })).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(b.ToArray().SequenceEqual(new byte[] { 20, 21 })).IsTrue();
        await Assert.That(third).IsTrue();
        await Assert.That(c.Length).IsEqualTo(0L);
        await Assert.That(fourth).IsFalse();
    }

    [Test]
    public async Task Returns_false_when_prefix_incomplete()
    {
        ReadOnlySequence<byte> buffer = new(new byte[] { 0, 0, 0 });
        await Assert.That(SpFraming.TryReadFrame(ref buffer, out _)).IsFalse();
    }

    [Test]
    public async Task Returns_false_when_body_incomplete()
    {
        ArrayBufferWriter<byte> writer = new();
        SpFraming.WriteFrame(writer, new byte[] { 1, 2, 3, 4 });
        byte[] truncated = writer.WrittenMemory.ToArray()[..^1];

        ReadOnlySequence<byte> buffer = new(truncated);
        await Assert.That(SpFraming.TryReadFrame(ref buffer, out _)).IsFalse();
    }

    [Test]
    public async Task Reassembles_a_frame_split_across_segments()
    {
        byte[] payload = Enumerable.Range(0, 200).Select(static i => (byte)i).ToArray();
        ArrayBufferWriter<byte> writer = new();
        SpFraming.WriteFrame(writer, payload);

        // 3-byte segments force the 8-byte prefix and the body to straddle segment boundaries.
        ReadOnlySequence<byte> buffer = SequenceHelper.Segmented(writer.WrittenMemory.ToArray(), 3);
        bool read = SpFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> body);

        await Assert.That(read).IsTrue();
        await Assert.That(body.ToArray().SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task Writes_sequence_body_identically_to_span_body()
    {
        byte[] payload = [7, 8, 9, 10, 11, 12];
        ArrayBufferWriter<byte> spanWriter = new();
        SpFraming.WriteFrame(spanWriter, payload);

        ReadOnlySequence<byte> segmented = SequenceHelper.Segmented(payload, 2);
        ArrayBufferWriter<byte> sequenceWriter = new();
        SpFraming.WriteFrame(sequenceWriter, segmented);

        await Assert.That(sequenceWriter.WrittenMemory.ToArray()
            .SequenceEqual(spanWriter.WrittenMemory.ToArray())).IsTrue();
    }

    [Test]
    public async Task Throws_when_body_exceeds_maximum()
    {
        ArrayBufferWriter<byte> writer = new();
        SpFraming.WriteFrame(writer, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        ReadOnlySequence<byte> buffer = new(writer.WrittenMemory);
        bool threw = false;
        try
        {
            SpFraming.TryReadFrame(ref buffer, out _, maxBodyLength: 4);
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
