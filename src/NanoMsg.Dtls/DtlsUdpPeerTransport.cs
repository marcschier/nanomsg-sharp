// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Threading.Channels;
using Dtls.Transport;

namespace NanoMsg.Dtls;

/// <summary>
/// The <see cref="IDatagramTransport"/> handed to <c>DtlsServer.AcceptAsync</c> for one accepted peer.
/// Outbound datagrams go through the listener's shared UDP socket to the peer's endpoint; inbound
/// datagrams are fed in by the listener's demultiplexing receive loop.
/// </summary>
internal sealed class DtlsUdpPeerTransport : IDatagramTransport
{
    private readonly DtlsUdpListener _listener;
    private readonly IPEndPoint _peer;
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="DtlsUdpPeerTransport"/> class.</summary>
    /// <param name="listener">The owning listener (shared send socket).</param>
    /// <param name="peer">The remote endpoint.</param>
    public DtlsUdpPeerTransport(DtlsUdpListener listener, IPEndPoint peer)
    {
        _listener = listener;
        _peer = peer;
    }

    /// <inheritdoc/>
    public int MaxDatagramSize => UdpDatagramTransport.MaxUdpPayload;

    /// <inheritdoc/>
    public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken) =>
        _listener.SendToAsync(datagram, _peer, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        byte[] datagram;
        try
        {
            datagram = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new IOException("The DTLS peer transport was closed.");
        }

        datagram.CopyTo(buffer.Span);
        return datagram.Length;
    }

    /// <summary>Enqueues a received datagram for this peer (called by the listener's receive loop).</summary>
    /// <param name="datagram">A copy of the received datagram.</param>
    internal void Enqueue(byte[] datagram) => _inbound.Writer.TryWrite(datagram);

    /// <summary>Completes the inbound queue so pending and future receives fail.</summary>
    internal void Complete() => _inbound.Writer.TryComplete();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inbound.Writer.TryComplete();
            _listener.Remove(_peer);
        }
    }
}
