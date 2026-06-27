// Copyright (c) marcschier. Licensed under the MIT License.

using System.Text;

namespace NanoMsg.Tests;

public sealed class UdpTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Udp_push_pull_roundtrips()
    {
        await using PullSocket pull = new();
        await using PushSocket push = new();
        int port = await pull.BindAsync("udp://127.0.0.1:0");
        push.Connect($"udp://127.0.0.1:{port}");
        await push.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await push.SendAsync("over-udp"u8.ToArray(), cts.Token);
        using NanoMessage message = await pull.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("over-udp");
    }

    [Test]
    public async Task Udp_pair_exchanges_both_ways()
    {
        await using PairSocket server = new();
        await using PairSocket client = new();
        int port = await server.BindAsync("udp://127.0.0.1:0");
        client.Connect($"udp://127.0.0.1:{port}");
        await server.WaitForConnectionsAsync(1, Timeout);
        await client.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await client.SendAsync("ping"u8.ToArray(), cts.Token);
        using (NanoMessage forward = await server.ReceiveAsync(cts.Token))
        {
            await Assert.That(Encoding.ASCII.GetString(forward.Span)).IsEqualTo("ping");
        }

        await server.SendAsync("pong"u8.ToArray(), cts.Token);
        using NanoMessage backward = await client.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(backward.Span)).IsEqualTo("pong");
    }

    [Test]
    public async Task Udp_req_rep_preserves_request_header()
    {
        await using ReplySocket rep = new();
        await using RequestSocket req = new();
        int port = await rep.BindAsync("udp://127.0.0.1:0");
        req.Connect($"udp://127.0.0.1:{port}");
        await req.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        Task server = Task.Run(async () =>
        {
            using NanoMessage request = await rep.ReceiveAsync(cts.Token);
            string body = "echo:" + Encoding.ASCII.GetString(request.Span);
            await rep.ReplyAsync(Encoding.ASCII.GetBytes(body), cts.Token);
        });

        using NanoMessage reply = await req.RequestAsync("hello"u8.ToArray(), cts.Token);
        await Assert.That(Encoding.ASCII.GetString(reply.Span)).IsEqualTo("echo:hello");
        await server;
    }

    [Test]
    public async Task Udp_carries_a_large_single_datagram_message()
    {
        await using PullSocket pull = new();
        await using PushSocket push = new();
        int port = await pull.BindAsync("udp://127.0.0.1:0");
        push.Connect($"udp://127.0.0.1:{port}");
        await push.WaitForConnectionsAsync(1, Timeout);

        // Stay under macOS's default UDP datagram limit (net.inet.udp.maxdgram, ~9216 bytes) so the
        // test is portable; the transport itself permits up to DatagramConnection.MaxDatagramPayload.
        byte[] payload = new byte[8000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 17 + 5);
        }

        using CancellationTokenSource cts = new(Timeout);
        await push.SendAsync(payload, cts.Token);
        using NanoMessage message = await pull.ReceiveAsync(cts.Token);
        await Assert.That(message.Span.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task Udp4_push_pull_roundtrips()
    {
        await using PullSocket pull = new();
        await using PushSocket push = new();
        int port = await pull.BindAsync("udp4://127.0.0.1:0");
        push.Connect($"udp4://127.0.0.1:{port}");
        await push.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await push.SendAsync("udp4"u8.ToArray(), cts.Token);
        using NanoMessage message = await pull.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("udp4");
    }
}
