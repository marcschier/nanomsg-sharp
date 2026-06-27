// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;

namespace NanoMsg.Tests;

/// <summary>Builds multi-segment <see cref="ReadOnlySequence{T}"/> instances for framing tests.</summary>
internal static class SequenceHelper
{
    /// <summary>
    /// Splits <paramref name="data"/> into a chain of <paramref name="segmentSize"/>-byte segments so
    /// that framing logic is exercised across segment boundaries.
    /// </summary>
    public static ReadOnlySequence<byte> Segmented(byte[] data, int segmentSize)
    {
        if (segmentSize <= 0 || data.Length <= segmentSize)
        {
            return new ReadOnlySequence<byte>(data);
        }

        Segment? first = null;
        Segment? last = null;
        for (int offset = 0; offset < data.Length; offset += segmentSize)
        {
            int length = Math.Min(segmentSize, data.Length - offset);
            ReadOnlyMemory<byte> memory = new(data, offset, length);
            if (first is null)
            {
                first = new Segment(memory, 0);
                last = first;
            }
            else
            {
                last = last!.Append(memory);
            }
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
