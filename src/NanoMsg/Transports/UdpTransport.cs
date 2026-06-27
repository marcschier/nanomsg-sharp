// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// The UDP datagram transport (<c>udp://</c>, with optional <c>udp4</c>/<c>udp6</c> family suffixes).
/// Each SP message maps to a single UDP packet (max <see cref="DatagramConnection.MaxDatagramPayload"/>
/// bytes); a lightweight connection request/ack handshake (modelled on NNG's experimental udp transport)
/// establishes a per-peer logical connection before data flows. This transport is unreliable and
/// unordered, like UDP itself. NNG interop is best-effort and not wire-verified (the experimental udp
/// transport is absent from released <c>libnng</c> packages).
/// </summary>
internal sealed class UdpTransport : INanoTransport
{
    internal const ushort RecvMax = DatagramConnection.MaxDatagramPayload;
    internal const ushort RefreshSeconds = 5;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    public ValueTask<INanoListener> BindAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        IPAddress ip = TcpEndpoints.BindAddress(address);
        Socket socket = new(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(ip, address.Port));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new NanoMsgException($"Failed to bind '{address.Original}': {ex.Message}", ex);
        }

        return new ValueTask<INanoListener>(new UdpListener(socket, localProtocol));
    }

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        IPEndPoint remote = await TcpEndpoints.ResolveRemoteAsync(address, cancellationToken).ConfigureAwait(false);
        Socket socket = new(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            await socket.ConnectAsync(remote, cancellationToken).ConfigureAwait(false);
            uint selfId = NewId();
            (SpProtocol peerProtocol, uint peerId) =
                await DialHandshakeAsync(socket, localProtocol, selfId, address, cancellationToken)
                    .ConfigureAwait(false);
            UdpDialerChannel channel = new(socket, localProtocol, selfId, peerId);
            return new DatagramConnection(channel, peerProtocol);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static uint NewId() =>
#if NETSTANDARD2_0 || NETSTANDARD2_1
        (uint)Polyfills.SharedRandom.Next(1, int.MaxValue);
#else
        (uint)Random.Shared.Next(1, int.MaxValue);
#endif

    private static async Task<(SpProtocol Peer, uint PeerId)> DialHandshakeAsync(
        Socket socket,
        SpProtocol local,
        uint selfId,
        NanoAddress address,
        CancellationToken cancellationToken)
    {
        byte[] creq = new byte[UdpSpHeader.Size];
        new UdpSpHeader(UdpSpHeader.OpCreq, local, RecvMax, RefreshSeconds, selfId, 0).WriteTo(creq);
        byte[] rx = new byte[UdpSpHeader.Size];

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConnectTimeout);
        try
        {
            while (true)
            {
                await socket.SendAsync(creq, SocketFlags.None, deadline.Token).ConfigureAwait(false);

                using CancellationTokenSource attempt =
                    CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(RetryInterval);
                try
                {
                    while (true)
                    {
                        int n = await socket.ReceiveAsync(rx, SocketFlags.None, attempt.Token).ConfigureAwait(false);
                        if (UdpSpHeader.TryParse(rx.AsSpan(0, n), out UdpSpHeader header) &&
                            header.OpCode == UdpSpHeader.OpCack && header.PeerId == selfId)
                        {
                            return (header.Protocol, header.SenderId);
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (attempt.IsCancellationRequested && !deadline.IsCancellationRequested)
                {
                    // Retry window elapsed without a CACK; resend the CREQ.
                }
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new NanoMsgException($"Timed out establishing udp connection to '{address.Original}'.");
        }
    }
}

/// <summary>
/// A <c>udp</c> listener: a single bound UDP socket whose receive loop demultiplexes inbound datagrams by
/// remote endpoint into per-peer <see cref="UdpPeerChannel"/> instances, completing the handshake and
/// surfacing each new peer as a <see cref="DatagramConnection"/>.
/// </summary>
internal sealed class UdpListener : INanoListener
{
    private readonly Socket _socket;
    private readonly SpProtocol _localProtocol;
    private readonly uint _selfId = UdpTransport.NewId();
    private readonly ConcurrentDictionary<IPEndPoint, UdpPeerChannel> _peers = new();
    private readonly Channel<INanoConnection> _accepted = Channel.CreateUnbounded<INanoConnection>();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _receiveLoop;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="UdpListener"/> class.</summary>
    /// <param name="socket">A bound UDP socket.</param>
    /// <param name="localProtocol">The local SP protocol.</param>
    public UdpListener(Socket socket, SpProtocol localProtocol)
    {
        _socket = socket;
        _localProtocol = localProtocol;
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
        foreach (UdpPeerChannel peer in _peers.Values)
        {
            peer.CompleteInbound();
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

    internal async ValueTask SendDataAsync(
        ReadOnlyMemory<byte> message,
        IPEndPoint peer,
        uint senderId,
        uint peerId,
        CancellationToken cancellationToken)
    {
        byte[] packet = ArrayPool<byte>.Shared.Rent(UdpSpHeader.Size + message.Length);
        try
        {
            new UdpSpHeader(UdpSpHeader.OpData, _localProtocol, (ushort)message.Length, 0, senderId, peerId)
                .WriteTo(packet);
            message.Span.CopyTo(packet.AsSpan(UdpSpHeader.Size));
            await SendRawAsync(packet.AsMemory(0, UdpSpHeader.Size + message.Length), peer, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    internal void Remove(IPEndPoint peer) => _peers.TryRemove(peer, out _);

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] rx = new byte[UdpSpHeader.Size + DatagramConnection.MaxDatagramPayload + 16];
        EndPoint any = new IPEndPoint(
            _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult result =
                    await _socket.ReceiveFromAsync(rx, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
                if (UdpSpHeader.TryParse(rx.AsSpan(0, result.ReceivedBytes), out UdpSpHeader header))
                {
                    IPEndPoint peer = (IPEndPoint)result.RemoteEndPoint;
                    await HandleAsync(header, rx, result.ReceivedBytes, peer, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task HandleAsync(
        UdpSpHeader header,
        byte[] rx,
        int length,
        IPEndPoint peer,
        CancellationToken cancellationToken)
    {
        switch (header.OpCode)
        {
            case UdpSpHeader.OpCreq:
                if (_peers.TryGetValue(peer, out UdpPeerChannel? existing))
                {
                    await SendControlAsync(UdpSpHeader.OpCack, _selfId, existing.PeerId, peer, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                UdpPeerChannel channel = new(this, peer, _selfId, header.SenderId);
                if (_peers.TryAdd(peer, channel))
                {
                    await SendControlAsync(UdpSpHeader.OpCack, _selfId, header.SenderId, peer, cancellationToken)
                        .ConfigureAwait(false);
                    await _accepted.Writer
                        .WriteAsync(new DatagramConnection(channel, header.Protocol), cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            case UdpSpHeader.OpData:
                if (_peers.TryGetValue(peer, out UdpPeerChannel? target) &&
                    UdpSpHeader.Size + header.Param0 <= length)
                {
                    target.Enqueue(rx.AsSpan(UdpSpHeader.Size, header.Param0).ToArray());
                }

                break;

            case UdpSpHeader.OpDisc:
                if (_peers.TryRemove(peer, out UdpPeerChannel? closing))
                {
                    closing.CompleteInbound();
                }

                break;
        }
    }

    private async ValueTask SendControlAsync(
        byte opCode,
        uint senderId,
        uint peerId,
        IPEndPoint peer,
        CancellationToken cancellationToken)
    {
        byte[] packet = new byte[UdpSpHeader.Size];
        ushort refresh = opCode is UdpSpHeader.OpCreq or UdpSpHeader.OpCack ? UdpTransport.RefreshSeconds : (ushort)0;
        new UdpSpHeader(opCode, _localProtocol, UdpTransport.RecvMax, refresh, senderId, peerId).WriteTo(packet);
        await SendRawAsync(packet, peer, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendRawAsync(
        ReadOnlyMemory<byte> packet,
        IPEndPoint peer,
        CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendToAsync(packet, SocketFlags.None, peer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

/// <summary>A per-peer datagram channel on a <see cref="UdpListener"/>; the listener feeds its inbound queue.</summary>
internal sealed class UdpPeerChannel : IDatagramChannel
{
    private readonly UdpListener _listener;
    private readonly IPEndPoint _peer;
    private readonly uint _selfId;
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="UdpPeerChannel"/> class.</summary>
    /// <param name="listener">The owning listener (used for the shared send socket).</param>
    /// <param name="peer">The remote endpoint.</param>
    /// <param name="selfId">Our connection id.</param>
    /// <param name="peerId">The peer's connection id.</param>
    public UdpPeerChannel(UdpListener listener, IPEndPoint peer, uint selfId, uint peerId)
    {
        _listener = listener;
        _peer = peer;
        _selfId = selfId;
        PeerId = peerId;
    }

    /// <summary>Gets the peer's connection id.</summary>
    public uint PeerId { get; }

    /// <inheritdoc/>
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        _listener.SendDataAsync(message, _peer, _selfId, PeerId, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    internal void Enqueue(byte[] payload) => _inbound.Writer.TryWrite(payload);

    internal void CompleteInbound() => _inbound.Writer.TryComplete();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inbound.Writer.TryComplete();
            _listener.Remove(_peer);
        }

        return default;
    }
}

/// <summary>The dialer side of the UDP transport: a single connected UDP socket to one peer.</summary>
internal sealed class UdpDialerChannel : IDatagramChannel
{
    private readonly Socket _socket;
    private readonly SpProtocol _localProtocol;
    private readonly uint _selfId;
    private readonly uint _peerId;
    private readonly byte[] _rx = new byte[UdpSpHeader.Size + DatagramConnection.MaxDatagramPayload + 16];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="UdpDialerChannel"/> class.</summary>
    /// <param name="socket">The connected UDP socket.</param>
    /// <param name="localProtocol">The local SP protocol.</param>
    /// <param name="selfId">Our connection id.</param>
    /// <param name="peerId">The peer's connection id (from the CACK).</param>
    public UdpDialerChannel(Socket socket, SpProtocol localProtocol, uint selfId, uint peerId)
    {
        _socket = socket;
        _localProtocol = localProtocol;
        _selfId = selfId;
        _peerId = peerId;
    }

    /// <inheritdoc/>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        byte[] packet = ArrayPool<byte>.Shared.Rent(UdpSpHeader.Size + message.Length);
        try
        {
            new UdpSpHeader(UdpSpHeader.OpData, _localProtocol, (ushort)message.Length, 0, _selfId, _peerId)
                .WriteTo(packet);
            message.Span.CopyTo(packet.AsSpan(UdpSpHeader.Size));
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(
                    packet.AsMemory(0, UdpSpHeader.Size + message.Length), SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int n;
            try
            {
                n = await _socket.ReceiveAsync(_rx, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return null;
            }

            if (!UdpSpHeader.TryParse(_rx.AsSpan(0, n), out UdpSpHeader header))
            {
                continue;
            }

            if (header.OpCode == UdpSpHeader.OpDisc)
            {
                return null;
            }

            if (header.OpCode == UdpSpHeader.OpData && UdpSpHeader.Size + header.Param0 <= n)
            {
                return _rx.AsMemory(UdpSpHeader.Size, header.Param0);
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

        try
        {
            byte[] disc = new byte[UdpSpHeader.Size];
            new UdpSpHeader(UdpSpHeader.OpDisc, _localProtocol, 0, 0, _selfId, _peerId).WriteTo(disc);
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(disc, SocketFlags.None).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        _socket.Dispose();
        _sendLock.Dispose();
    }
}
