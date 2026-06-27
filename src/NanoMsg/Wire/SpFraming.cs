// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;

namespace NanoMsg.Wire;

/// <summary>
/// Length-prefixed message framing for the stream transports (tcp, ipc): each message body is
/// preceded by its length as a 64-bit big-endian integer. The one-time <see cref="SpHeader"/>
/// handshake is exchanged separately, before any framed messages flow.
/// </summary>
internal static class SpFraming
{
    /// <summary>The size of the big-endian length prefix, in bytes.</summary>
    public const int LengthPrefixSize = 8;

    /// <summary>The size of the 4-byte request/survey id header used by REQ/REP and SURVEY.</summary>
    public const int HeaderSize = 4;

    /// <summary>Writes <paramref name="body"/> as a single length-prefixed frame.</summary>
    /// <param name="writer">The destination buffer writer (typically a <c>PipeWriter</c>).</param>
    /// <param name="body">The message payload.</param>
    public static void WriteFrame(IBufferWriter<byte> writer, ReadOnlySpan<byte> body)
    {
        Span<byte> prefix = writer.GetSpan(LengthPrefixSize);
        BinaryPrimitives.WriteUInt64BigEndian(prefix, (ulong)body.Length);
        writer.Advance(LengthPrefixSize);
        writer.Write(body);
    }

    /// <summary>Writes a length-prefixed frame whose body is a possibly multi-segment sequence.</summary>
    /// <param name="writer">The destination buffer writer.</param>
    /// <param name="body">The message payload.</param>
    public static void WriteFrame(IBufferWriter<byte> writer, in ReadOnlySequence<byte> body)
    {
        Span<byte> prefix = writer.GetSpan(LengthPrefixSize);
        BinaryPrimitives.WriteUInt64BigEndian(prefix, (ulong)body.Length);
        writer.Advance(LengthPrefixSize);
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            writer.Write(segment.Span);
        }
    }

    /// <summary>
    /// Writes a length-prefixed frame whose body is a 4-byte big-endian <paramref name="header"/>
    /// (a request or survey id) followed by <paramref name="body"/>. The header and body are written
    /// straight into the writer with no intermediate concatenation buffer.
    /// </summary>
    /// <param name="writer">The destination buffer writer.</param>
    /// <param name="header">The 4-byte protocol header to prepend.</param>
    /// <param name="body">The message payload.</param>
    public static void WriteFrame(IBufferWriter<byte> writer, uint header, ReadOnlySpan<byte> body)
    {
        int total = HeaderSize + body.Length;
        Span<byte> prefix = writer.GetSpan(LengthPrefixSize + HeaderSize);
        BinaryPrimitives.WriteUInt64BigEndian(prefix, (ulong)total);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.Slice(LengthPrefixSize), header);
        writer.Advance(LengthPrefixSize + HeaderSize);
        writer.Write(body);
    }

    /// <summary>
    /// Attempts to read one complete frame from the front of <paramref name="buffer"/>. On success,
    /// <paramref name="buffer"/> is advanced past the consumed frame and <paramref name="body"/> is
    /// sliced over the original buffer — it remains valid only until the owning <c>PipeReader</c> is
    /// advanced.
    /// </summary>
    /// <param name="buffer">The buffer to read from; advanced past the frame on success.</param>
    /// <param name="body">The framed message body, sliced over <paramref name="buffer"/>.</param>
    /// <param name="maxBodyLength">The maximum permitted body length; longer frames throw.</param>
    /// <returns><see langword="true"/> if a complete frame was available.</returns>
    /// <exception cref="NanoMsgException">
    /// The advertised body length exceeds <paramref name="maxBodyLength"/>.
    /// </exception>
    public static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer,
        out ReadOnlySequence<byte> body,
        long maxBodyLength = long.MaxValue)
    {
        body = default;
        if (buffer.Length < LengthPrefixSize)
        {
            return false;
        }

        Span<byte> lengthBytes = stackalloc byte[LengthPrefixSize];
        buffer.Slice(0, LengthPrefixSize).CopyTo(lengthBytes);
        ulong length = BinaryPrimitives.ReadUInt64BigEndian(lengthBytes);

        if (maxBodyLength >= 0 && length > (ulong)maxBodyLength)
        {
            throw new NanoMsgException(
                $"Inbound message length {length} exceeds the configured maximum of {maxBodyLength} bytes.");
        }

        if ((ulong)(buffer.Length - LengthPrefixSize) < length)
        {
            return false;
        }

        body = buffer.Slice(LengthPrefixSize, (long)length);
        buffer = buffer.Slice(body.End);
        return true;
    }
}
