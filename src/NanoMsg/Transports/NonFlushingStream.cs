// Copyright (c) marcschier. Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace NanoMsg.Transports;

/// <summary>
/// A pass-through <see cref="Stream"/> decorator whose <see cref="Flush"/>/<see cref="FlushAsync"/>
/// are no-ops. Windows named pipes map stream flush to <c>FlushFileBuffers</c>, which blocks until the
/// peer has drained the pipe; with both handshake ends flushing at once that deadlocks. The bytes are
/// already delivered by the write methods, so dropping
/// the flush is safe and removes the stall.
/// </summary>
#if NET5_0_OR_GREATER
[ExcludeFromCodeCoverage(Justification = "Windows-only; covered by the Windows build-test job.")]
#else
[ExcludeFromCodeCoverage]
#endif
internal sealed class NonFlushingStream : Stream
{
    private readonly Stream _inner;

    /// <summary>Initializes a new instance of the <see cref="NonFlushingStream"/> class.</summary>
    /// <param name="inner">The wrapped, owned stream.</param>
    public NonFlushingStream(Stream inner) => _inner = inner;

    /// <inheritdoc/>
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

#if !NETSTANDARD2_0
    /// <inheritdoc/>
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);
#endif

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

#if !NETSTANDARD2_0
    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);
#endif

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
