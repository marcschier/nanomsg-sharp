// Copyright (c) marcschier. Licensed under the MIT License.

using System.IO.Pipelines;

namespace NanoMsg.Transports;

/// <summary>
/// Adapts a <see cref="Stream"/> (a <see cref="System.Net.Sockets.NetworkStream"/> over a TCP or
/// Unix-domain socket, or a named-pipe stream) into an <see cref="INanoConnection"/> by layering
/// <see cref="PipeReader"/>/<see cref="PipeWriter"/> over it. The stream is owned and disposed by
/// this connection.
/// </summary>
internal sealed class StreamConnection : INanoConnection
{
    private readonly Stream _stream;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="StreamConnection"/> class.</summary>
    /// <param name="stream">The owned underlying stream.</param>
    public StreamConnection(Stream stream)
    {
        _stream = stream;
        Input = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    /// <inheritdoc/>
    public PipeReader Input { get; }

    /// <inheritdoc/>
    public PipeWriter Output { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Input.CompleteAsync().ConfigureAwait(false);
        await Output.CompleteAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
