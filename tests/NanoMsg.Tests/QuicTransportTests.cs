// Copyright (c) marcschier. Licensed under the MIT License.

// QuicConnection.IsSupported is part of the [Experimental]/preview System.Net.Quic surface; the tests
// only probe it to skip cleanly where MsQuic is unavailable (for example macOS, or a Linux runner
// without libmsquic).
#pragma warning disable SYSLIB5001
#pragma warning disable CA2252
#pragma warning disable CA1416

using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NanoMsg.Tests;

public sealed class QuicTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static bool QuicUnavailable => !QuicConnection.IsSupported;

    [Test]
    public async Task Quic_push_pull_roundtrips()
    {
        if (QuicUnavailable)
        {
            return;
        }

        using X509Certificate2 cert = TestTls.CreateSelfSignedCertificate();
        NanoSocketOptions serverOptions = new() { TlsServerCertificate = cert };
        NanoSocketOptions clientOptions = new() { TlsRemoteValidationCallback = TestTls.AcceptAnyCertificate() };

        await using PullSocket pull = new(serverOptions);
        await using PushSocket push = new(clientOptions);
        int port = await pull.BindAsync("quic://127.0.0.1:0");
        push.Connect($"quic://127.0.0.1:{port}");
        await push.WaitForConnectionsAsync(1, Timeout);

        using CancellationTokenSource cts = new(Timeout);
        await push.SendAsync("over-quic"u8.ToArray(), cts.Token);
        using NanoMessage message = await pull.ReceiveAsync(cts.Token);
        await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("over-quic");
    }

    [Test]
    public async Task Quic_pair_exchanges_both_ways()
    {
        if (QuicUnavailable)
        {
            return;
        }

        using X509Certificate2 cert = TestTls.CreateSelfSignedCertificate();
        NanoSocketOptions serverOptions = new() { TlsServerCertificate = cert };
        NanoSocketOptions clientOptions = new() { TlsRemoteValidationCallback = TestTls.AcceptAnyCertificate() };

        await using PairSocket server = new(serverOptions);
        await using PairSocket client = new(clientOptions);
        int port = await server.BindAsync("quic://127.0.0.1:0");
        client.Connect($"quic://127.0.0.1:{port}");
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
    public async Task Quic_req_rep_roundtrips()
    {
        if (QuicUnavailable)
        {
            return;
        }

        using X509Certificate2 cert = TestTls.CreateSelfSignedCertificate();
        NanoSocketOptions serverOptions = new() { TlsServerCertificate = cert };
        NanoSocketOptions clientOptions = new() { TlsRemoteValidationCallback = TestTls.AcceptAnyCertificate() };

        await using ReplySocket rep = new(serverOptions);
        await using RequestSocket req = new(clientOptions);
        int port = await rep.BindAsync("quic://127.0.0.1:0");
        req.Connect($"quic://127.0.0.1:{port}");
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
    public async Task Binding_quic_without_certificate_throws()
    {
        if (QuicUnavailable)
        {
            return;
        }

        await using PullSocket pull = new();
        bool threw = false;
        try
        {
            await pull.BindAsync("quic://127.0.0.1:0");
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
