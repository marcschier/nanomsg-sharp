// Copyright (c) marcschier. Licensed under the MIT License.

using NanoMsg.Protocols;

namespace NanoMsg;

/// <summary>
/// An opaque handle to a connected pair1 peer, obtained from <see cref="Pair1Socket.ReceiveFromAsync"/>
/// and passed back to <see cref="Pair1Socket.SendToAsync"/> for directed (polyamorous) replies.
/// </summary>
public readonly struct PairPeer : IEquatable<PairPeer>
{
    internal PairPeer(NanoPipe pipe) => Pipe = pipe;

    internal NanoPipe? Pipe { get; }

    /// <summary>Gets a value indicating whether this handle references a peer.</summary>
    public bool IsValid => Pipe is not null;

    /// <summary>Determines whether two handles reference the same peer.</summary>
    public static bool operator ==(PairPeer left, PairPeer right) => left.Equals(right);

    /// <summary>Determines whether two handles reference different peers.</summary>
    public static bool operator !=(PairPeer left, PairPeer right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(PairPeer other) => ReferenceEquals(Pipe, other.Pipe);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PairPeer other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Pipe?.GetHashCode() ?? 0;
}

/// <summary>
/// NNG pair1 socket: a peer-to-peer socket whose messages carry a hop-count header (loop protection)
/// and which can connect to multiple peers (polyamorous). Use <see cref="SendAsync"/> to reach any
/// peer, or <see cref="ReceiveFromAsync"/> + <see cref="SendToAsync"/> for directed exchanges. For
/// interoperability with the legacy nanomsg PAIR (version 0), use <see cref="PairSocket"/> instead.
/// </summary>
public sealed class Pair1Socket : NanoSocket
{
    private readonly Pair1Core _core;

    /// <summary>Initializes a new <see cref="Pair1Socket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public Pair1Socket(NanoSocketOptions? options = null)
        : this(new Pair1Core(options))
    {
    }

    private Pair1Socket(Pair1Core core)
        : base(core) => _core = core;

    /// <summary>Sends <paramref name="body"/> to any connected peer.</summary>
    /// <param name="body">The message payload.</param>
    /// <param name="cancellationToken">A token used to cancel the send.</param>
    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        _core.SendAsync(body, cancellationToken);

    /// <summary>Sends <paramref name="body"/> to a specific peer (a directed/polyamorous reply).</summary>
    /// <param name="peer">The peer handle from <see cref="ReceiveFromAsync"/>.</param>
    /// <param name="body">The message payload.</param>
    /// <param name="cancellationToken">A token used to cancel the send.</param>
    /// <exception cref="NanoMsgException">The peer handle is not valid.</exception>
    public ValueTask SendToAsync(
        PairPeer peer,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        if (peer.Pipe is null)
        {
            throw new NanoMsgException("The pair peer handle is not valid.");
        }

        return _core.SendToAsync(peer.Pipe, body, cancellationToken);
    }

    /// <summary>Receives the next message; dispose the returned message when done.</summary>
    /// <param name="cancellationToken">A token used to cancel the receive.</param>
    /// <returns>The received message.</returns>
    public async ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        InboundMessage inbound = await _core.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return inbound.Message;
    }

    /// <summary>Receives the next message along with a handle to the peer that sent it.</summary>
    /// <param name="cancellationToken">A token used to cancel the receive.</param>
    /// <returns>The received message and the originating peer handle.</returns>
    public async ValueTask<(NanoMessage Message, PairPeer Peer)> ReceiveFromAsync(
        CancellationToken cancellationToken = default)
    {
        InboundMessage inbound = await _core.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return (inbound.Message, new PairPeer(inbound.Source));
    }
}
