// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net.Sockets;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// The TCP transport. <c>bind</c> creates a listening socket (a host of <c>*</c> binds all
/// interfaces; port 0 lets the OS choose, surfaced via <see cref="INanoListener.Port"/>); <c>connect</c>
/// resolves the host (IP literal or DNS) and dials it.
/// </summary>
internal sealed class TcpTransport : INanoTransport
{
    /// <inheritdoc/>
    public ValueTask<INanoListener> BindAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        Socket socket = TcpEndpoints.Listen(address);
        return new ValueTask<INanoListener>(new SocketListener(socket, options.TcpNoDelay));
    }

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        Socket socket = await TcpEndpoints.ConnectAsync(address, options.TcpNoDelay, cancellationToken)
            .ConfigureAwait(false);
        return new StreamConnection(new NetworkStream(socket, ownsSocket: true));
    }
}
