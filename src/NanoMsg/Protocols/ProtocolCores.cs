// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using NanoMsg.Wire;

namespace NanoMsg.Protocols;

/// <summary>PAIR: a single bidirectional peer. Sends to the peer and fair-queues inbound messages.</summary>
internal sealed class PairCore : NanoSocketCore
{
    public PairCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Pair, options)
    {
    }

    protected override bool DeliversInbound => true;

    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        SendRoundRobinAsync(body, cancellationToken);

    public ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        ReceivePayloadAsync(cancellationToken);
}

/// <summary>PUB: broadcasts every message to all connected subscribers. Receive-disabled.</summary>
internal sealed class PubCore : NanoSocketCore
{
    public PubCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Pub, options)
    {
    }

    protected override bool DeliversInbound => false;

    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        BroadcastAsync(body, cancellationToken);
}

/// <summary>
/// SUB: receives messages from publishers, delivering only those whose payload starts with one of the
/// registered subscription prefixes (an empty prefix matches everything). Filtering is local, matching
/// the reference nanomsg wire behaviour (no subscription messages are sent to the publisher).
/// </summary>
internal sealed class SubCore : NanoSocketCore
{
    private readonly object _subscriptionGate = new();
    private readonly List<byte[]> _subscriptions = [];

    public SubCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Sub, options)
    {
    }

    protected override bool DeliversInbound => true;

    public void Subscribe(ReadOnlySpan<byte> prefix)
    {
        byte[] copy = prefix.ToArray();
        lock (_subscriptionGate)
        {
            _subscriptions.Add(copy);
        }
    }

    public void Unsubscribe(ReadOnlySpan<byte> prefix)
    {
        lock (_subscriptionGate)
        {
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                if (_subscriptions[i].AsSpan().SequenceEqual(prefix))
                {
                    _subscriptions.RemoveAt(i);
                    return;
                }
            }
        }
    }

    public ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        ReceivePayloadAsync(cancellationToken);

    protected override bool ShouldDeliver(NanoPipe pipe, in ReadOnlySequence<byte> body)
    {
        lock (_subscriptionGate)
        {
            foreach (byte[] subscription in _subscriptions)
            {
                if (StartsWith(body, subscription))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>PUSH: load-balances each message to one peer in round-robin order. Receive-disabled.</summary>
internal sealed class PushCore : NanoSocketCore
{
    public PushCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Push, options)
    {
    }

    protected override bool DeliversInbound => false;

    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        SendRoundRobinAsync(body, cancellationToken);
}

/// <summary>PULL: fair-queues inbound messages from all connected pushers. Send-disabled.</summary>
internal sealed class PullCore : NanoSocketCore
{
    public PullCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Pull, options)
    {
    }

    protected override bool DeliversInbound => true;

    public ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        ReceivePayloadAsync(cancellationToken);
}

/// <summary>
/// BUS: broadcasts each message to all directly-connected peers and fair-queues inbound messages from
/// all peers. A node never receives its own sends (there is no automatic forwarding), so the
/// "everyone except the origin" delivery rule holds without any per-message routing header.
/// </summary>
internal sealed class BusCore : NanoSocketCore
{
    public BusCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Bus, options)
    {
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        BroadcastAsync(body, cancellationToken);

    public ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        ReceivePayloadAsync(cancellationToken);
}
