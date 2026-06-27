// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;

namespace NanoMsg;

/// <summary>
/// An owned, pooled-buffer copy of a received message payload. A copy is taken at the receive
/// ownership boundary — where the payload must outlive the transport's pipe buffer — and the buffer
/// is returned to the pool when the message is disposed. Always dispose received messages (an
/// <c>await using</c> or <c>using</c> declaration is convenient) to avoid pool exhaustion.
/// </summary>
public sealed class NanoMessage : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;

    private NanoMessage(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    /// <summary>Gets the message payload.</summary>
    public ReadOnlyMemory<byte> Payload => _buffer is null
        ? throw new ObjectDisposedException(nameof(NanoMessage))
        : _buffer.AsMemory(0, _length);

    /// <summary>Gets the message payload as a span.</summary>
    public ReadOnlySpan<byte> Span => Payload.Span;

    /// <summary>Copies <paramref name="source"/> into a pooled buffer.</summary>
    /// <param name="source">The frame body, sliced over a transport pipe buffer.</param>
    /// <returns>An owned copy of the payload.</returns>
    internal static NanoMessage CopyFrom(in ReadOnlySequence<byte> source)
    {
        int length = checked((int)source.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        source.CopyTo(buffer);
        return new NanoMessage(buffer, length);
    }

    /// <summary>Returns the pooled buffer to the shared pool.</summary>
    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
