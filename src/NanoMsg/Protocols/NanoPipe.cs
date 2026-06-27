// Copyright (c) marcschier. Licensed under the MIT License.

using System.IO.Pipelines;
using NanoMsg.Transports;
using NanoMsg.Wire;

namespace NanoMsg.Protocols;

/// <summary>
/// One connected peer of a socket: an established, handshaked <see cref="INanoConnection"/> with a
/// serialized framed-send path. Sends write the length-prefixed frame straight into the connection's
/// <see cref="PipeWriter"/> (zero staging copy); a per-pipe lock serializes concurrent senders.
/// </summary>
internal sealed class NanoPipe : IAsyncDisposable
{
    private readonly INanoConnection _connection;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="NanoPipe"/> class.</summary>
    /// <param name="connection">The established connection.</param>
    /// <param name="peerProtocol">The protocol advertised by the peer during the handshake.</param>
    public NanoPipe(INanoConnection connection, SpProtocol peerProtocol)
    {
        _connection = connection;
        PeerProtocol = peerProtocol;
    }

    /// <summary>Gets the protocol advertised by the peer.</summary>
    public SpProtocol PeerProtocol { get; }

    /// <summary>Gets the inbound reader used by the socket's read loop.</summary>
    public PipeReader Input => _connection.Input;

    /// <summary>Frames and sends <paramref name="body"/> to the peer.</summary>
    /// <param name="body">The message payload.</param>
    /// <param name="cancellationToken">A token used to cancel the send.</param>
    /// <exception cref="NanoMsgException">The connection was closed by the peer.</exception>
    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
        SendCoreAsync(hasHeader: false, header: 0, body, cancellationToken);

    /// <summary>Frames and sends a 4-byte big-endian <paramref name="header"/> then <paramref name="body"/>.</summary>
    /// <param name="header">The request or survey id to prepend.</param>
    /// <param name="body">The message payload.</param>
    /// <param name="cancellationToken">A token used to cancel the send.</param>
    /// <exception cref="NanoMsgException">The connection was closed by the peer.</exception>
    public ValueTask SendAsync(uint header, ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
        SendCoreAsync(hasHeader: true, header, body, cancellationToken);

    private async ValueTask SendCoreAsync(
        bool hasHeader,
        uint header,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            throw new NanoMsgException("The connection was closed by the peer.");
        }

        try
        {
            if (hasHeader)
            {
                SpFraming.WriteFrame(_connection.Output, header, body.Span);
            }
            else
            {
                SpFraming.WriteFrame(_connection.Output, body.Span);
            }

            FlushResult result = await _connection.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsCompleted)
            {
                throw new NanoMsgException("The connection was closed by the peer.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The pipe writer was completed/disposed by a concurrent close; surface it as a closed peer.
            throw new NanoMsgException("The connection was closed by the peer.");
        }
        finally
        {
            try
            {
                _sendLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // The pipe was disposed concurrently; there is nothing to release.
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
