// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using NanoMsg.Transports;
using NanoMsg.Wire;

namespace NanoMsg.Tests.Transports;

public sealed class TransportTests
{
    [Test]
    [Arguments("inproc")]
    [Arguments("tcp")]
    [Arguments("ipc")]
    [Arguments("ws")]
    public async Task Loopback_roundtrips_framed_messages(string scheme)
    {
        await using Harness harness = await Harness.StartAsync(scheme, SpProtocol.Pair);
        (INanoConnection client, INanoConnection server) =
            await harness.ConnectAsync(SpProtocol.Pair, SpProtocol.Pair);

        await using (client)
        await using (server)
        {
            await SendFrameAsync(client, "ping"u8.ToArray());
            byte[] forward = await ReadOneFrameAsync(server);
            await Assert.That(forward.SequenceEqual("ping"u8.ToArray())).IsTrue();

            await SendFrameAsync(server, "pong"u8.ToArray());
            byte[] backward = await ReadOneFrameAsync(client);
            await Assert.That(backward.SequenceEqual("pong"u8.ToArray())).IsTrue();
        }
    }

    [Test]
    [Arguments("inproc")]
    [Arguments("tcp")]
    [Arguments("ipc")]
    [Arguments("ws")]
    public async Task Handshake_reports_peer_protocol(string scheme)
    {
        await using Harness harness = await Harness.StartAsync(scheme, SpProtocol.Rep);
        (INanoConnection client, INanoConnection server, SpProtocol clientPeer, SpProtocol serverPeer) =
            await harness.ConnectWithPeersAsync(SpProtocol.Req, SpProtocol.Rep);

        await using (client)
        await using (server)
        {
            await Assert.That(clientPeer).IsEqualTo(SpProtocol.Rep);
            await Assert.That(serverPeer).IsEqualTo(SpProtocol.Req);
        }
    }

    [Test]
    public async Task Tcp_assigns_ephemeral_port()
    {
        NanoAddress bind = NanoAddress.Parse("tcp://127.0.0.1:0");
        INanoTransport transport = TransportFactory.For(bind.Scheme);
        await using INanoListener listener =
            await transport.BindAsync(bind, new NanoSocketOptions(), SpProtocol.Pair, default);
        await Assert.That(listener.Port).IsGreaterThan(0);
    }

    [Test]
    public async Task Tcp4_binds_and_connects_over_ipv4()
    {
        NanoAddress bind = NanoAddress.Parse("tcp4://*:0");
        INanoTransport transport = TransportFactory.For(bind.Scheme);
        await using INanoListener listener =
            await transport.BindAsync(bind, new NanoSocketOptions(), SpProtocol.Pair, default);

        Task<INanoConnection> acceptTask = listener.AcceptAsync(default).AsTask();
        await using INanoConnection client = await transport.ConnectAsync(
            NanoAddress.Parse($"tcp4://127.0.0.1:{listener.Port}"), new NanoSocketOptions(), SpProtocol.Pair, default);
        await using INanoConnection server = await acceptTask;
        await Assert.That(server).IsNotNull();
    }

    [Test]
    public async Task Tcp_resolves_dns_host_on_connect()
    {
        // "localhost" can resolve to both ::1 and 127.0.0.1; the transport dials the first resolved
        // address, so bind to that same primary address to keep the test deterministic across hosts.
        IPAddress primary = (await Dns.GetHostAddressesAsync("localhost"))[0];
        string bindHost = primary.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{primary}]"
            : primary.ToString();

        NanoAddress bind = NanoAddress.Parse($"tcp://{bindHost}:0");
        INanoTransport transport = TransportFactory.For(bind.Scheme);
        await using INanoListener listener =
            await transport.BindAsync(bind, new NanoSocketOptions(), SpProtocol.Pair, default);

        Task<INanoConnection> acceptTask = listener.AcceptAsync(default).AsTask();
        await using INanoConnection client = await transport.ConnectAsync(
            NanoAddress.Parse($"tcp://localhost:{listener.Port}"), new NanoSocketOptions(), SpProtocol.Pair, default);
        await using INanoConnection server = await acceptTask;
        await Assert.That(server).IsNotNull();
    }

    [Test]
    public async Task Listener_accepts_multiple_connections()
    {
        await using Harness harness = await Harness.StartAsync("inproc", SpProtocol.Pair);
        (INanoConnection c1, INanoConnection s1) = await harness.ConnectAsync(SpProtocol.Pair, SpProtocol.Pair);
        (INanoConnection c2, INanoConnection s2) = await harness.ConnectAsync(SpProtocol.Pair, SpProtocol.Pair);

        await using (c1)
        await using (s1)
        await using (c2)
        await using (s2)
        {
            await SendFrameAsync(c1, [1]);
            await SendFrameAsync(c2, [2]);
            byte[] first = await ReadOneFrameAsync(s1);
            byte[] second = await ReadOneFrameAsync(s2);
            await Assert.That(first[0]).IsEqualTo((byte)1);
            await Assert.That(second[0]).IsEqualTo((byte)2);
        }
    }

    [Test]
    public async Task Handshake_rejects_incompatible_protocols()
    {
        await using Harness harness = await Harness.StartAsync("inproc", SpProtocol.Rep);

        bool threw = false;
        try
        {
            (INanoConnection client, INanoConnection server) =
                await harness.ConnectAsync(SpProtocol.Pub, SpProtocol.Rep);
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Connecting_to_unbound_inproc_endpoint_is_refused()
    {
        InProcTransport transport = new();
        bool threw = false;
        try
        {
            await transport.ConnectAsync(
                NanoAddress.Parse($"inproc://missing-{Guid.NewGuid():N}"),
                new NanoSocketOptions(),
                SpProtocol.Pair,
                default);
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Dtls_without_package_throws_helpful_error()
    {
        await using PushSocket push = new();
        NanoMsgException? caught = null;
        try
        {
            await push.BindAsync("dtls+udp://127.0.0.1:0");
        }
        catch (NanoMsgException ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message.Contains("NanoMsgSharp.Dtls")).IsTrue();
    }

    private static async Task SendFrameAsync(INanoConnection connection, byte[] payload)
    {
        SpFraming.WriteFrame(connection.Output, payload);
        await connection.Output.FlushAsync();
    }

    private static async Task<byte[]> ReadOneFrameAsync(INanoConnection connection)
    {
        while (true)
        {
            ReadResult result = await connection.Input.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (SpFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> body))
            {
                byte[] payload = body.ToArray();
                connection.Input.AdvanceTo(buffer.Start);
                return payload;
            }

            connection.Input.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                throw new InvalidOperationException("Connection closed before a full frame arrived.");
            }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly INanoTransport _transport;
        private readonly INanoListener _listener;
        private readonly string _connectAddress;

        private Harness(INanoTransport transport, INanoListener listener, string connectAddress)
        {
            _transport = transport;
            _listener = listener;
            _connectAddress = connectAddress;
        }

        public static async Task<Harness> StartAsync(string scheme, SpProtocol serverProtocol)
        {
            string id = Guid.NewGuid().ToString("N");
            string bind = scheme switch
            {
                "inproc" => $"inproc://{id}",
                "tcp" => "tcp://127.0.0.1:0",
                "ws" => "ws://127.0.0.1:0",
                "ipc" => OperatingSystem.IsWindows()
                    ? $"ipc://nano-{id}"
                    : $"ipc://{Path.Combine(Path.GetTempPath(), $"nano-{id}.sock")}",
                _ => throw new ArgumentOutOfRangeException(nameof(scheme)),
            };

            NanoAddress bindAddress = NanoAddress.Parse(bind);
            INanoTransport transport = TransportFactory.For(bindAddress.Scheme);
            INanoListener listener =
                await transport.BindAsync(bindAddress, new NanoSocketOptions(), serverProtocol, default);
            string connect = scheme is "tcp" or "ws" ? $"{scheme}://127.0.0.1:{listener.Port}" : bind;
            return new Harness(transport, listener, connect);
        }

        public async Task<(INanoConnection Client, INanoConnection Server)> ConnectAsync(
            SpProtocol clientProtocol,
            SpProtocol serverProtocol)
        {
            (INanoConnection client, INanoConnection server, _, _) =
                await ConnectWithPeersAsync(clientProtocol, serverProtocol);
            return (client, server);
        }

        public async Task<(
            INanoConnection Client, INanoConnection Server, SpProtocol ClientPeer, SpProtocol ServerPeer)>
            ConnectWithPeersAsync(SpProtocol clientProtocol, SpProtocol serverProtocol)
        {
            Task<INanoConnection> acceptTask = _listener.AcceptAsync(default).AsTask();
            INanoConnection client = await _transport.ConnectAsync(
                NanoAddress.Parse(_connectAddress), new NanoSocketOptions(), clientProtocol, default);
            INanoConnection server = await acceptTask;

            Task<SpProtocol> clientHandshake = SpHandshake.PerformAsync(client, clientProtocol, default).AsTask();
            Task<SpProtocol> serverHandshake = SpHandshake.PerformAsync(server, serverProtocol, default).AsTask();
            await Task.WhenAll(clientHandshake, serverHandshake);

            return (client, server, await clientHandshake, await serverHandshake);
        }

        public ValueTask DisposeAsync() => _listener.DisposeAsync();
    }
}
