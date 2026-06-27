// Copyright (c) marcschier. Licensed under the MIT License.

using System.IO.Pipelines;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// A bidirectional byte channel to a single connected peer, exposed as a duplex
/// <see cref="PipeReader"/>/<see cref="PipeWriter"/> pair. The SP handshake and length-prefix
/// framing run on top of this raw byte channel, so every transport (inproc, tcp, ipc, ws) presents
/// the identical interface to the protocol layer.
/// </summary>
internal interface INanoConnection : IDuplexPipe, IAsyncDisposable
{
}

/// <summary>A bound endpoint that accepts incoming <see cref="INanoConnection"/> instances.</summary>
internal interface INanoListener : IAsyncDisposable
{
    /// <summary>Gets the resolved local port for <c>tcp</c>/<c>ws</c> listeners; 0 for other transports.</summary>
    int Port { get; }

    /// <summary>Accepts the next inbound connection.</summary>
    /// <param name="cancellationToken">A token used to cancel the wait.</param>
    /// <returns>The accepted connection.</returns>
    ValueTask<INanoConnection> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>Creates listeners (<c>bind</c>) and connections (<c>connect</c>) for a transport scheme.</summary>
internal interface INanoTransport
{
    /// <summary>Starts listening at <paramref name="address"/>.</summary>
    /// <param name="address">The local address to bind.</param>
    /// <param name="options">Socket options (TLS certificates, etc.).</param>
    /// <param name="localProtocol">The SP protocol advertised by this endpoint (used by <c>ws</c>/<c>wss</c>).</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A listener accepting inbound connections.</returns>
    ValueTask<INanoListener> BindAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken);

    /// <summary>Dials <paramref name="address"/> and returns a connected duplex byte channel.</summary>
    /// <param name="address">The remote address to connect to.</param>
    /// <param name="options">Socket options (TLS certificates, etc.).</param>
    /// <param name="localProtocol">The SP protocol advertised by this endpoint (used by <c>ws</c>/<c>wss</c>).</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The established connection.</returns>
    ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken);
}
