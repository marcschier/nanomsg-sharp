// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;

namespace NanoMsg.Transports;

/// <summary>
/// An <see cref="INanoListener"/> backed by a listening <see cref="Socket"/>, shared by the TCP and
/// Unix-domain (ipc) transports. Each accepted socket is wrapped in a <see cref="NetworkStream"/> and
/// surfaced as a <see cref="StreamConnection"/>.
/// </summary>
internal sealed class SocketListener : INanoListener
{
    private readonly Socket _socket;
    private readonly bool _noDelay;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="SocketListener"/> class.</summary>
    /// <param name="socket">A bound, listening socket.</param>
    /// <param name="noDelay">Whether to disable Nagle's algorithm on accepted TCP sockets.</param>
    public SocketListener(Socket socket, bool noDelay = false)
    {
        _socket = socket;
        _noDelay = noDelay;
    }

    /// <inheritdoc/>
    public int Port => (_socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        Socket accepted = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
        if (_noDelay)
        {
            accepted.NoDelay = true;
        }

        return new StreamConnection(new NetworkStream(accepted, ownsSocket: true));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _socket.Dispose();
        }

        return default;
    }
}
