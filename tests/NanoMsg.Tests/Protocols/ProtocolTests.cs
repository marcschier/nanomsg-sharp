// Copyright (c) marcschier. Licensed under the MIT License.

using NanoMsg.Protocols;

namespace NanoMsg.Tests.Protocols;

public sealed class ProtocolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments("inproc")]
    [Arguments("tcp")]
    public async Task Pair_exchanges_messages_both_ways(string scheme)
    {
        await using PairCore left = new();
        await using PairCore right = new();
        string connect = await BindAndConnectAddress(scheme, left);
        right.Connect(connect);
        await left.WaitForPipesAsync(1, Timeout, default);
        await right.WaitForPipesAsync(1, Timeout, default);

        await right.SendAsync("hello"u8.ToArray());
        byte[] atLeft = await Receive(left.ReceiveAsync);
        await Assert.That(atLeft.SequenceEqual("hello"u8.ToArray())).IsTrue();

        await left.SendAsync("world"u8.ToArray());
        byte[] atRight = await Receive(right.ReceiveAsync);
        await Assert.That(atRight.SequenceEqual("world"u8.ToArray())).IsTrue();
    }

    [Test]
    public async Task Sub_receives_only_matching_prefixes()
    {
        string address = $"inproc://pubsub-{Guid.NewGuid():N}";
        await using PubCore pub = new();
        await using SubCore sub = new();
        await pub.BindAsync(address);
        sub.Connect(address);
        sub.Subscribe("topic"u8);
        await sub.WaitForPipesAsync(1, Timeout, default);
        await pub.WaitForPipesAsync(1, Timeout, default);

        await pub.SendAsync("topic-A"u8.ToArray());
        await pub.SendAsync("ignored"u8.ToArray());
        await pub.SendAsync("topic-B"u8.ToArray());

        byte[] first = await Receive(sub.ReceiveAsync);
        byte[] second = await Receive(sub.ReceiveAsync);
        await Assert.That(first.SequenceEqual("topic-A"u8.ToArray())).IsTrue();
        await Assert.That(second.SequenceEqual("topic-B"u8.ToArray())).IsTrue();
    }

    [Test]
    public async Task Sub_with_empty_subscription_receives_everything()
    {
        string address = $"inproc://pubsub-all-{Guid.NewGuid():N}";
        await using PubCore pub = new();
        await using SubCore sub = new();
        await pub.BindAsync(address);
        sub.Connect(address);
        sub.Subscribe(ReadOnlySpan<byte>.Empty);
        await pub.WaitForPipesAsync(1, Timeout, default);

        await pub.SendAsync("anything"u8.ToArray());
        byte[] got = await Receive(sub.ReceiveAsync);
        await Assert.That(got.SequenceEqual("anything"u8.ToArray())).IsTrue();
    }

    [Test]
    public async Task Push_load_balances_across_pullers()
    {
        string address = $"inproc://pipeline-{Guid.NewGuid():N}";
        await using PushCore push = new();
        await using PullCore pullA = new();
        await using PullCore pullB = new();
        await push.BindAsync(address);
        pullA.Connect(address);
        pullB.Connect(address);
        await push.WaitForPipesAsync(2, Timeout, default);

        for (int i = 0; i < 4; i++)
        {
            await push.SendAsync(new[] { (byte)i });
        }

        List<byte> received =
        [
            (await Receive(pullA.ReceiveAsync))[0],
            (await Receive(pullA.ReceiveAsync))[0],
            (await Receive(pullB.ReceiveAsync))[0],
            (await Receive(pullB.ReceiveAsync))[0],
        ];

        received.Sort();
        await Assert.That(received.SequenceEqual(new byte[] { 0, 1, 2, 3 })).IsTrue();
    }

    [Test]
    public async Task Pull_fair_queues_from_multiple_pushers()
    {
        string address = $"inproc://fanin-{Guid.NewGuid():N}";
        await using PullCore pull = new();
        await using PushCore pushA = new();
        await using PushCore pushB = new();
        await pull.BindAsync(address);
        pushA.Connect(address);
        pushB.Connect(address);
        await pushA.WaitForPipesAsync(1, Timeout, default);
        await pushB.WaitForPipesAsync(1, Timeout, default);

        await pushA.SendAsync("from-a"u8.ToArray());
        await pushB.SendAsync("from-b"u8.ToArray());

        List<string> received =
        [
            System.Text.Encoding.ASCII.GetString(await Receive(pull.ReceiveAsync)),
            System.Text.Encoding.ASCII.GetString(await Receive(pull.ReceiveAsync)),
        ];

        received.Sort();
        await Assert.That(received[0]).IsEqualTo("from-a");
        await Assert.That(received[1]).IsEqualTo("from-b");
    }

    private static async Task<byte[]> Receive(Func<CancellationToken, ValueTask<NanoMessage>> receive)
    {
        using CancellationTokenSource cts = new(Timeout);
        using NanoMessage message = await receive(cts.Token);
        return message.Payload.ToArray();
    }

    private static async Task<string> BindAndConnectAddress(string scheme, NanoSocketCore binder)
    {
        if (scheme == "tcp")
        {
            int port = await binder.BindAsync("tcp://127.0.0.1:0");
            return $"tcp://127.0.0.1:{port}";
        }

        string address = $"inproc://{scheme}-{Guid.NewGuid():N}";
        await binder.BindAsync(address);
        return address;
    }
}
