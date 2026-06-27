// Copyright (c) marcschier. Licensed under the MIT License.

namespace NanoMsg.Transports;

/// <summary>
/// A message-oriented secure-or-plain datagram channel to a single peer: each send/receive carries one
/// whole SP message (no length prefix — the datagram boundary delimits it). It is the substrate the
/// <see cref="DatagramConnection"/> adapts to the byte-pipe <see cref="INanoConnection"/> contract, and
/// is implemented over raw UDP (core) and over DTLS (the <c>NanoMsgSharp.Dtls</c> package).
/// </summary>
internal interface IDatagramChannel : IAsyncDisposable
{
    /// <summary>Sends <paramref name="message"/> as a single datagram payload.</summary>
    /// <param name="message">One whole SP message (already including any per-protocol header).</param>
    /// <param name="cancellationToken">A token used to cancel the send.</param>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>Receives the next datagram payload, or <see langword="null"/> once the peer closed.</summary>
    /// <param name="cancellationToken">A token used to cancel the receive.</param>
    /// <returns>The received SP message, or <see langword="null"/> at end of stream.</returns>
    ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken);
}
