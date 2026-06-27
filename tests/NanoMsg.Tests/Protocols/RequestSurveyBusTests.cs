// Copyright (c) marcschier. Licensed under the MIT License.

using System.Text;
using NanoMsg.Protocols;

namespace NanoMsg.Tests.Protocols;

public sealed class RequestSurveyBusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Req_rep_round_trips_a_request()
    {
        string address = $"inproc://reqrep-{Guid.NewGuid():N}";
        await using RepCore rep = new();
        await using ReqCore req = new();
        await rep.BindAsync(address);
        req.Connect(address);

        using CancellationTokenSource cts = new(Timeout);
        Task responder = RespondOnceAsync(rep, request => request + "-reply", cts.Token);

        using NanoMessage reply = await req.RequestAsync("ping"u8.ToArray(), cts.Token);
        await Assert.That(Encoding.ASCII.GetString(reply.Payload.Span)).IsEqualTo("ping-reply");
        await responder;
    }

    [Test]
    public async Task Req_correlates_concurrent_requests_by_id()
    {
        string address = $"inproc://reqrep-many-{Guid.NewGuid():N}";
        await using RepCore rep = new();
        await using ReqCore req = new();
        await rep.BindAsync(address);
        req.Connect(address);

        using CancellationTokenSource cts = new(Timeout);
        Task responder = Task.Run(
            async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    using RepExchange exchange = await rep.ReceiveAsync(cts.Token);
                    string request = Encoding.ASCII.GetString(exchange.Request.Payload.Span);
                    await exchange.ReplyAsync(Encoding.ASCII.GetBytes(request + "!"), cts.Token);
                }
            },
            cts.Token);

        Task<NanoMessage> one = req.RequestAsync("one"u8.ToArray(), cts.Token).AsTask();
        Task<NanoMessage> two = req.RequestAsync("two"u8.ToArray(), cts.Token).AsTask();
        Task<NanoMessage> three = req.RequestAsync("three"u8.ToArray(), cts.Token).AsTask();
        NanoMessage[] replies = await Task.WhenAll(one, two, three);

        List<string> texts = [.. replies.Select(static r => Encoding.ASCII.GetString(r.Payload.Span))];
        foreach (NanoMessage reply in replies)
        {
            reply.Dispose();
        }

        texts.Sort();
        await Assert.That(string.Join(",", texts)).IsEqualTo("one!,three!,two!");
        await responder;
    }

    [Test]
    public async Task Surveyor_collects_responses_from_all_respondents()
    {
        string address = $"inproc://survey-{Guid.NewGuid():N}";
        NanoSocketOptions options = new() { SurveyDeadline = TimeSpan.FromSeconds(1) };
        await using SurveyorCore surveyor = new(options);
        await using RespondentCore first = new();
        await using RespondentCore second = new();
        await surveyor.BindAsync(address);
        first.Connect(address);
        second.Connect(address);
        await surveyor.WaitForPipesAsync(2, Timeout, default);

        using CancellationTokenSource cts = new(Timeout);
        Task r1 = RespondOnceAsync(first, "r1", cts.Token);
        Task r2 = RespondOnceAsync(second, "r2", cts.Token);

        IReadOnlyList<NanoMessage> responses = await surveyor.SurveyAsync("question"u8.ToArray(), cts.Token);
        List<string> texts = [.. responses.Select(static r => Encoding.ASCII.GetString(r.Payload.Span))];
        foreach (NanoMessage response in responses)
        {
            response.Dispose();
        }

        texts.Sort();
        await Assert.That(string.Join(",", texts)).IsEqualTo("r1,r2");
        await Task.WhenAll(r1, r2);
    }

    [Test]
    public async Task Bus_delivers_between_peers_without_self_delivery()
    {
        string address = $"inproc://bus-{Guid.NewGuid():N}";
        await using BusCore a = new();
        await using BusCore b = new();
        await a.BindAsync(address);
        b.Connect(address);
        await a.WaitForPipesAsync(1, Timeout, default);
        await b.WaitForPipesAsync(1, Timeout, default);

        using CancellationTokenSource cts = new(Timeout);
        await a.SendAsync("from-a"u8.ToArray());
        using NanoMessage atB = await b.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(atB.Payload.Span)).IsEqualTo("from-a");

        await b.SendAsync("from-b"u8.ToArray());
        using NanoMessage atA = await a.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(atA.Payload.Span)).IsEqualTo("from-b");
    }

    private static Task RespondOnceAsync(
        RepCore rep,
        Func<string, string> reply,
        CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                using RepExchange exchange = await rep.ReceiveAsync(cancellationToken);
                string request = Encoding.ASCII.GetString(exchange.Request.Payload.Span);
                await exchange.ReplyAsync(Encoding.ASCII.GetBytes(reply(request)), cancellationToken);
            },
            cancellationToken);

    private static Task RespondOnceAsync(
        RespondentCore respondent,
        string response,
        CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                using RespondentExchange exchange = await respondent.ReceiveAsync(cancellationToken);
                await exchange.RespondAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
            },
            cancellationToken);
}
