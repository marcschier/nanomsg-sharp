// Copyright (c) marcschier. Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NanoMsg.Tests;

public sealed class WebSocketTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Ws_push_pull_roundtrips()
    {
        await using PullSocket pull = new();
        await using PushSocket push = new();
        int port = await pull.BindAsync("ws://127.0.0.1:0");
        push.Connect($"ws://127.0.0.1:{port}");
        await push.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await push.SendAsync("over-ws"u8.ToArray(), cts.Token);
        using NanoMessage message = await pull.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("over-ws");
    }

    [Test]
    public async Task Ws_pair_exchanges_both_ways()
    {
        await using PairSocket server = new();
        await using PairSocket client = new();
        int port = await server.BindAsync("ws://127.0.0.1:0");
        client.Connect($"ws://127.0.0.1:{port}");
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
    public async Task Ws_req_rep_preserves_request_header()
    {
        await using ReplySocket rep = new();
        await using RequestSocket req = new();
        int port = await rep.BindAsync("ws://127.0.0.1:0");
        req.Connect($"ws://127.0.0.1:{port}");
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
    public async Task Wss_push_pull_roundtrips()
    {
        using X509Certificate2 cert = TestTls.CreateSelfSignedCertificate();
        NanoSocketOptions serverOptions = new() { TlsServerCertificate = cert };
        NanoSocketOptions clientOptions = new()
        {
            TlsRemoteValidationCallback = TestTls.AcceptAnyCertificate(),
        };

        await using PullSocket pull = new(serverOptions);
        await using PushSocket push = new(clientOptions);
        int port = await pull.BindAsync("wss://127.0.0.1:0");
        push.Connect($"wss://127.0.0.1:{port}");
        await push.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await push.SendAsync("over-wss"u8.ToArray(), cts.Token);
        using NanoMessage message = await pull.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("over-wss");
    }

    [Test]
    public async Task Ws_handles_large_multisegment_message()
    {
        await using PullSocket pull = new();
        await using PushSocket push = new();
        int port = await pull.BindAsync("ws://127.0.0.1:0");
        push.Connect($"ws://127.0.0.1:{port}");
        await push.WaitForConnectionsAsync(1, Timeout);

        byte[] payload = new byte[256 * 1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 + 7);
        }

        using CancellationTokenSource cts = new(Timeout);
        await push.SendAsync(payload, cts.Token);
        using NanoMessage message = await pull.ReceiveAsync(cts.Token);
        await Assert.That(message.Span.SequenceEqual(payload)).IsTrue();
    }

    [Test]
    public async Task Ws_pub_sub_roundtrips()
    {
        await using PublishSocket pub = new();
        await using SubscribeSocket sub = new();
        int port = await pub.BindAsync("ws://127.0.0.1:0");
        sub.Connect($"ws://127.0.0.1:{port}");
        sub.Subscribe("topic");
        await pub.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        ValueTask<NanoMessage> pending = sub.ReceiveAsync(cts.Token);
        for (int i = 0; i < 20 && !pending.IsCompleted; i++)
        {
            await pub.SendAsync("topic:ws"u8.ToArray(), cts.Token);
            await Task.Delay(50, cts.Token);
        }

        using NanoMessage message = await pending;
        await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("topic:ws");
    }

    [Test]
    public async Task Ws_bus_and_survey_roundtrip()
    {
        await using BusSocket hub = new();
        await using BusSocket peer = new();
        int busPort = await hub.BindAsync("ws://127.0.0.1:0");
        peer.Connect($"ws://127.0.0.1:{busPort}");
        await hub.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await hub.SendAsync("bus-ws"u8.ToArray(), cts.Token);
        using (NanoMessage busMessage = await peer.ReceiveAsync(cts.Token))
        {
            await Assert.That(Encoding.ASCII.GetString(busMessage.Span)).IsEqualTo("bus-ws");
        }

        NanoSocketOptions surveyorOptions = new() { SurveyDeadline = TimeSpan.FromSeconds(2) };
        await using SurveyorSocket surveyor = new(surveyorOptions);
        await using RespondentSocket respondent = new();
        int surveyPort = await surveyor.BindAsync("ws://127.0.0.1:0");
        respondent.Connect($"ws://127.0.0.1:{surveyPort}");
        await surveyor.WaitForConnectionsAsync(1, Timeout);

        Task responder = Task.Run(async () =>
        {
            using NanoMessage survey = await respondent.ReceiveAsync(cts.Token);
            await respondent.RespondAsync("answer-ws"u8.ToArray(), cts.Token);
        });

        IReadOnlyList<NanoMessage> responses = await surveyor.SurveyAsync("q-ws"u8.ToArray(), cts.Token);
        try
        {
            bool answered = responses.Any(r => Encoding.ASCII.GetString(r.Span) == "answer-ws");
            await Assert.That(answered).IsTrue();
        }
        finally
        {
            foreach (NanoMessage response in responses)
            {
                response.Dispose();
            }
        }

        await responder;
    }

    [Test]
    public async Task Ws_pair1_exchanges_both_ways()
    {
        await using Pair1Socket left = new();
        await using Pair1Socket right = new();
        int port = await left.BindAsync("ws://127.0.0.1:0");
        right.Connect($"ws://127.0.0.1:{port}");
        await left.WaitForConnectionsAsync(1, Timeout);
        await right.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await left.SendAsync("p1-ws"u8.ToArray(), cts.Token);
        using NanoMessage message = await right.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("p1-ws");
    }

    [Test]
    public async Task Ws_server_rejects_non_websocket_request()
    {
        await using PullSocket pull = new();
        int port = await pull.BindAsync("ws://127.0.0.1:0");

        using System.Net.Sockets.TcpClient client = new();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        await using System.Net.Sockets.NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await stream.WriteAsync(request);

        byte[] buffer = new byte[256];
        using CancellationTokenSource cts = new(Timeout);
        int read = await stream.ReadAsync(buffer, cts.Token);
        string response = Encoding.ASCII.GetString(buffer, 0, read);
        await Assert.That(response.Contains("400")).IsTrue();
    }

    [Test]
    public async Task Binding_wss_without_certificate_throws()
    {
        await using PullSocket pull = new();
        bool threw = false;
        try
        {
            await pull.BindAsync("wss://127.0.0.1:0");
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
