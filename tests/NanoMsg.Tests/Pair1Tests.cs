// Copyright (c) marcschier. Licensed under the MIT License.

using System.Text;

namespace NanoMsg.Tests;

public sealed class Pair1Tests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Pair1_exchanges_both_ways()
    {
        string address = $"inproc://pair1-{Guid.NewGuid():N}";
        await using Pair1Socket left = new();
        await using Pair1Socket right = new();
        await left.BindAsync(address);
        right.Connect(address);
        await left.WaitForConnectionsAsync(1, Timeout);
        await right.WaitForConnectionsAsync(1, Timeout);

        await left.SendAsync("ping"u8.ToArray());
        await Assert.That(await Receive(right)).IsEqualTo("ping");

        await right.SendAsync("pong"u8.ToArray());
        await Assert.That(await Receive(left)).IsEqualTo("pong");
    }

    [Test]
    public async Task Pair1_directed_reply_targets_the_sending_peer()
    {
        string address = $"inproc://pair1-poly-{Guid.NewGuid():N}";
        await using Pair1Socket hub = new();
        await using Pair1Socket peerA = new();
        await using Pair1Socket peerB = new();
        await hub.BindAsync(address);
        peerA.Connect(address);
        peerB.Connect(address);
        await hub.WaitForConnectionsAsync(2, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await peerA.SendAsync("from-a"u8.ToArray());

        (NanoMessage message, PairPeer peer) = await hub.ReceiveFromAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("from-a");
        await Assert.That(peer.IsValid).IsTrue();
        message.Dispose();

        await hub.SendToAsync(peer, "directed"u8.ToArray());
        await Assert.That(await Receive(peerA)).IsEqualTo("directed");
    }

    private static async Task<string> Receive(Pair1Socket socket)
    {
        using CancellationTokenSource cts = new(Timeout);
        using NanoMessage message = await socket.ReceiveAsync(cts.Token);
        return Encoding.ASCII.GetString(message.Span);
    }

    [Test]
    public async Task PairPeer_default_is_invalid_and_equatable()
    {
        PairPeer a = default;
        PairPeer b = default;

        await Assert.That(a.IsValid).IsFalse();
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != b).IsFalse();
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals((object)b)).IsTrue();
        await Assert.That(a.Equals("not-a-peer")).IsFalse();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task Pair1_directed_send_to_invalid_peer_throws()
    {
        await using Pair1Socket socket = new();
        bool threw = false;
        try
        {
            await socket.SendToAsync(default, "x"u8.ToArray());
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
