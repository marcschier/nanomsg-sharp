// Copyright (c) marcschier. Licensed under the MIT License.

using System.Text;

namespace NanoMsg.Tests;

public sealed class PublicApiCoverageTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Pair_socket_exchanges_both_ways()
    {
        string address = $"inproc://api-pair-{Guid.NewGuid():N}";
        await using PairSocket left = new();
        await using PairSocket right = new();
        await left.BindAsync(address);
        right.Connect(address);
        await left.WaitForConnectionsAsync(1, Timeout);
        await right.WaitForConnectionsAsync(1, Timeout);

        await left.SendAsync("ping"u8.ToArray());
        await Assert.That(await ReceiveText(right.ReceiveAsync)).IsEqualTo("ping");
        await right.SendAsync("pong"u8.ToArray());
        await Assert.That(await ReceiveText(left.ReceiveAsync)).IsEqualTo("pong");
    }

    [Test]
    public async Task Bus_socket_exchanges_both_ways()
    {
        string address = $"inproc://api-bus-{Guid.NewGuid():N}";
        await using BusSocket a = new();
        await using BusSocket b = new();
        await a.BindAsync(address);
        b.Connect(address);
        await a.WaitForConnectionsAsync(1, Timeout);
        await b.WaitForConnectionsAsync(1, Timeout);

        await a.SendAsync("from-a"u8.ToArray());
        await Assert.That(await ReceiveText(b.ReceiveAsync)).IsEqualTo("from-a");
        await b.SendAsync("from-b"u8.ToArray());
        await Assert.That(await ReceiveText(a.ReceiveAsync)).IsEqualTo("from-b");
    }

    [Test]
    public async Task Surveyor_respondent_round_trips()
    {
        string address = $"inproc://api-survey-{Guid.NewGuid():N}";
        NanoSocketOptions options = new() { SurveyDeadline = TimeSpan.FromSeconds(2) };
        await using SurveyorSocket surveyor = new(options);
        await using RespondentSocket respondent = new();
        await surveyor.BindAsync(address);
        respondent.Connect(address);
        await surveyor.WaitForConnectionsAsync(1, Timeout);
        await respondent.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task responder = Task.Run(
            async () =>
            {
                try
                {
                    while (true)
                    {
                        using NanoMessage survey = await respondent.ReceiveAsync(cts.Token);
                        await respondent.RespondAsync("answer"u8.ToArray(), cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            cts.Token);

        // A survey is one-shot and deadline-bound, so retry until the (single) respondent is heard.
        IReadOnlyList<NanoMessage> responses = await surveyor.SurveyAsync("q"u8.ToArray(), cts.Token);
        while (responses.Count == 0)
        {
            responses = await surveyor.SurveyAsync("q"u8.ToArray(), cts.Token);
        }

        try
        {
            await Assert.That(responses.Count).IsEqualTo(1);
            await Assert.That(Encoding.ASCII.GetString(responses[0].Span)).IsEqualTo("answer");
        }
        finally
        {
            foreach (NanoMessage response in responses)
            {
                response.Dispose();
            }

            await cts.CancelAsync();
        }

        try
        {
            await responder;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Test]
    public async Task Respond_before_receive_throws()
    {
        await using RespondentSocket respondent = new();
        bool threw = false;
        try
        {
            await respondent.RespondAsync("x"u8.ToArray());
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Unsubscribe_removes_the_filter()
    {
        string address = $"inproc://api-unsub-{Guid.NewGuid():N}";
        await using PublishSocket pub = new();
        await using SubscribeSocket sub = new();
        await pub.BindAsync(address);
        sub.Connect(address);
        sub.Subscribe("a");
        sub.Subscribe("b");
        sub.Unsubscribe("a");
        await pub.WaitForConnectionsAsync(1, Timeout);

        // "a-..." is filtered now; only "b-..." is delivered.
        await pub.SendAsync("a-dropped"u8.ToArray());
        await pub.SendAsync("b-kept"u8.ToArray());
        await Assert.That(await ReceiveText(sub.ReceiveAsync)).IsEqualTo("b-kept");
    }

    [Test]
    public async Task Connect_retries_until_a_late_bind_succeeds()
    {
        string address = $"inproc://api-latebind-{Guid.NewGuid():N}";
        await using ReplySocket rep = new();
        await using RequestSocket req = new();

        // Connect before anything is bound: the connect loop must retry until the bind appears.
        req.Connect(address);
        await Task.Delay(200);

        using CancellationTokenSource cts = new(Timeout);
        await rep.BindAsync(address);
        Task responder = Task.Run(
            async () =>
            {
                using NanoMessage request = await rep.ReceiveAsync(cts.Token);
                await rep.ReplyAsync("late-ok"u8.ToArray(), cts.Token);
            },
            cts.Token);

        using NanoMessage reply = await req.RequestAsync("ping"u8.ToArray(), cts.Token);
        await Assert.That(Encoding.ASCII.GetString(reply.Span)).IsEqualTo("late-ok");
        await responder;
    }

    [Test]
    public async Task NanoMsgException_constructors_are_usable()
    {
        NanoMsgException empty = new();
        NanoMsgException withInner = new("boom", new InvalidOperationException("cause"));
        await Assert.That(empty.Message.Length).IsGreaterThan(0);
        await Assert.That(withInner.InnerException).IsNotNull();
    }

    [Test]
    public async Task Request_reconnects_after_reply_socket_restarts()
    {
        string address = $"inproc://api-reconnect-{Guid.NewGuid():N}";
        await using RequestSocket req = new();
        req.Connect(address);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        await using (ReplySocket first = new())
        {
            await first.BindAsync(address);
            Task firstResponder = Task.Run(
                async () =>
                {
                    using NanoMessage request = await first.ReceiveAsync(cts.Token);
                    await first.ReplyAsync("one"u8.ToArray(), cts.Token);
                },
                cts.Token);

            using NanoMessage firstReply = await req.RequestAsync("a"u8.ToArray(), cts.Token);
            await Assert.That(Encoding.ASCII.GetString(firstReply.Span)).IsEqualTo("one");
            await firstResponder;
        }

        // The first reply socket has dropped; the request socket must reconnect to a fresh one.
        await using ReplySocket second = new();
        await second.BindAsync(address);
        Task secondResponder = Task.Run(
            async () =>
            {
                using NanoMessage request = await second.ReceiveAsync(cts.Token);
                await second.ReplyAsync("two"u8.ToArray(), cts.Token);
            },
            cts.Token);

        using NanoMessage secondReply = await req.RequestAsync("b"u8.ToArray(), cts.Token);
        await Assert.That(Encoding.ASCII.GetString(secondReply.Span)).IsEqualTo("two");
        await secondResponder;
    }

    private static async Task<string> ReceiveText(Func<CancellationToken, ValueTask<NanoMessage>> receive)
    {
        using CancellationTokenSource cts = new(Timeout);
        using NanoMessage message = await receive(cts.Token);
        return Encoding.ASCII.GetString(message.Span);
    }
}
