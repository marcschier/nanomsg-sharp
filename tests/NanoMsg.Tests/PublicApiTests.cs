// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.Text;

namespace NanoMsg.Tests;

public sealed class PublicApiTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Publish_subscribe_filters_by_prefix()
    {
        string address = $"inproc://api-pubsub-{Guid.NewGuid():N}";
        await using PublishSocket pub = new();
        await using SubscribeSocket sub = new();
        await pub.BindAsync(address);
        sub.Connect(address);
        sub.Subscribe("news");
        await pub.WaitForConnectionsAsync(1, Timeout);

        await pub.SendAsync("news:hello"u8.ToArray());
        await pub.SendAsync("sports:skip"u8.ToArray());
        await pub.SendAsync("news:world"u8.ToArray());

        await Assert.That(await ReceiveText(sub.ReceiveAsync)).IsEqualTo("news:hello");
        await Assert.That(await ReceiveText(sub.ReceiveAsync)).IsEqualTo("news:world");
    }

    [Test]
    public async Task Request_reply_round_trips()
    {
        string address = $"inproc://api-reqrep-{Guid.NewGuid():N}";
        await using ReplySocket rep = new();
        await using RequestSocket req = new();
        await rep.BindAsync(address);
        req.Connect(address);

        using CancellationTokenSource cts = new(Timeout);
        Task responder = Task.Run(
            async () =>
            {
                using NanoMessage request = await rep.ReceiveAsync(cts.Token);
                string text = Encoding.ASCII.GetString(request.Span);
                await rep.ReplyAsync(Encoding.ASCII.GetBytes(text + "-ok"), cts.Token);
            },
            cts.Token);

        using NanoMessage reply = await req.RequestAsync("status"u8.ToArray(), cts.Token);
        await Assert.That(Encoding.ASCII.GetString(reply.Span)).IsEqualTo("status-ok");
        await responder;
    }

    [Test]
    public async Task Push_pull_delivers_messages()
    {
        string address = $"inproc://api-pipeline-{Guid.NewGuid():N}";
        await using PushSocket push = new();
        await using PullSocket pull = new();
        await push.BindAsync(address);
        pull.Connect(address);

        await push.SendAsync("work"u8.ToArray());
        await Assert.That(await ReceiveText(pull.ReceiveAsync)).IsEqualTo("work");
    }

    [Test]
    public async Task Reply_before_receive_throws()
    {
        await using ReplySocket rep = new();
        bool threw = false;
        try
        {
            await rep.ReplyAsync("x"u8.ToArray());
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Disposed_message_payload_throws()
    {
        NanoMessage message = NanoMessage.CopyFrom(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }));
        await Assert.That(message.Payload.Length).IsEqualTo(3);
        message.Dispose();

        bool threw = false;
        try
        {
            _ = message.Payload;
        }
        catch (ObjectDisposedException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task CopyFrom_handles_an_empty_payload()
    {
        using NanoMessage message = NanoMessage.CopyFrom(default);

        await Assert.That(message.Payload.Length).IsEqualTo(0);
        await Assert.That(message.Span.Length).IsEqualTo(0);
    }

    private static async Task<string> ReceiveText(Func<CancellationToken, ValueTask<NanoMessage>> receive)
    {
        using CancellationTokenSource cts = new(Timeout);
        using NanoMessage message = await receive(cts.Token);
        return Encoding.ASCII.GetString(message.Span);
    }
}
