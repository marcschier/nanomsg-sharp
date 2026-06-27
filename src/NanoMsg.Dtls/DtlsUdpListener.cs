// Copyright (c) marcschier. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Dtls;
using NanoMsg.Transports;
using NanoMsg.Wire;

namespace NanoMsg.Dtls;

/// <summary>
/// A <c>dtls+udp</c> listener: a single bound UDP socket whose receive loop demultiplexes inbound
/// datagrams by remote endpoint, drives a DTLS server handshake per peer (over a
/// <see cref="DtlsUdpPeerTransport"/>), negotiates the SP protocol, and surfaces each established peer
/// as a <see cref="DatagramConnection"/>.
/// </summary>
internal sealed class DtlsUdpListener : INanoListener
{
    private readonly Socket _socket;
    private readonly SpProtocol _localProtocol;
    private readonly DtlsServer _server;
    private readonly ConcurrentDictionary<IPEndPoint, DtlsUdpPeerTransport> _peers = new();
    private readonly Channel<INanoConnection> _accepted = Channel.CreateUnbounded<INanoConnection>();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _receiveLoop;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="DtlsUdpListener"/> class.</summary>
    /// <param name="socket">A bound UDP socket.</param>
    /// <param name="options">Socket options carrying the DTLS server certificate.</param>
    /// <param name="localProtocol">The local SP protocol.</param>
    public DtlsUdpListener(Socket socket, NanoSocketOptions options, SpProtocol localProtocol)
    {
        _socket = socket;
        _localProtocol = localProtocol;
        _server = new DtlsServer(DtlsSpExchange.BuildServerOptions(options));
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_shutdown.Token));
    }

    /// <inheritdoc/>
    public int Port => (_socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> AcceptAsync(CancellationToken cancellationToken) =>
        await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _accepted.Writer.TryComplete();
        foreach (DtlsUdpPeerTransport peer in _peers.Values)
        {
            peer.Complete();
        }

        _socket.Dispose();
        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _sendLock.Dispose();
        _shutdown.Dispose();
    }

    internal async ValueTask SendToAsync(
        ReadOnlyMemory<byte> datagram,
        IPEndPoint peer,
        CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendToAsync(datagram, SocketFlags.None, peer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    internal void Remove(IPEndPoint peer) => _peers.TryRemove(peer, out _);

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] rx = new byte[DatagramConnection.MaxDatagramPayload + 2048];
        EndPoint any = new IPEndPoint(
            _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult result =
                    await _socket.ReceiveFromAsync(rx, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
                IPEndPoint peer = (IPEndPoint)result.RemoteEndPoint;
                byte[] datagram = rx.AsSpan(0, result.ReceivedBytes).ToArray();

                if (_peers.TryGetValue(peer, out DtlsUdpPeerTransport? existing))
                {
                    existing.Enqueue(datagram);
                    continue;
                }

                DtlsUdpPeerTransport transport = new(this, peer);
                if (_peers.TryAdd(peer, transport))
                {
                    transport.Enqueue(datagram);
                    _ = HandshakeAsync(transport, peer, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task HandshakeAsync(
        DtlsUdpPeerTransport transport,
        IPEndPoint peer,
        CancellationToken cancellationToken)
    {
        DtlsConnection? connection = null;
        try
        {
            connection = await _server.AcceptAsync(transport, cancellationToken).ConfigureAwait(false);
            SpProtocol peerProtocol =
                await DtlsSpExchange.PerformAsync(connection, _localProtocol, cancellationToken).ConfigureAwait(false);
            DatagramConnection nano = new(new DtlsDatagramChannel(connection, transport), peerProtocol);
            await _accepted.Writer.WriteAsync(nano, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DtlsException or IOException or SocketException or NanoMsgException
            or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            connection?.Dispose();
            transport.Dispose();
            _peers.TryRemove(peer, out _);
        }
    }
}
