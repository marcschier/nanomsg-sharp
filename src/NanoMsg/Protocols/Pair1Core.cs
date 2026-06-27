// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using NanoMsg.Wire;

namespace NanoMsg.Protocols;

/// <summary>
/// NNG pair1: a PAIR socket whose messages carry a 32-bit header (low byte = a hop-count TTL,
/// initialized to 1) used with device topologies for loop protection. It supports multiple connected
/// peers: <see cref="SendAsync"/> targets any available peer (round-robin), while
/// <see cref="SendToAsync"/> targets a specific peer (typically the one a message arrived on).
/// </summary>
internal sealed class Pair1Core : NanoSocketCore
{
    private const uint InitialTtl = 1;

    public Pair1Core(NanoSocketOptions? options = null)
        : base(SpProtocol.Pair1, options)
    {
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        SendRoundRobinAsync(InitialTtl, body, cancellationToken);

    public ValueTask SendToAsync(
        NanoPipe peer,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposedCore();
        return TrySendToPipeAsync(peer, InitialTtl, body, cancellationToken);
    }

    public ValueTask<InboundMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        ReceiveInboundAsync(cancellationToken);

    protected override void OnFrame(NanoPipe pipe, in ReadOnlySequence<byte> frame)
    {
        if (!TryReadHeader(frame, out _, out ReadOnlySequence<byte> payload))
        {
            return;
        }

        EnqueueInbound(new InboundMessage(pipe, NanoMessage.CopyFrom(payload)));
    }
}
