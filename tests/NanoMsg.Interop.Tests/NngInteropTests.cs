// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text;
using NativeSocket = NanoMsg.Interop.Tests.NativeNng.NngSocket;

namespace NanoMsg.Interop.Tests;

/// <summary>
/// Wire-compatibility tests against the reference C <c>libnng</c> (nanomsg-next-gen). A NanoMsgSharp
/// socket on one side and a real NNG socket (driven via <see cref="NativeNng"/>) on the other must
/// complete each scalability-protocol exchange over every shared transport (tcp, ipc, ws), confirming
/// that NanoMsgSharp speaks both nanomsg and NNG. The tests skip when <c>libnng</c> is unavailable.
/// </summary>
public sealed class NngInteropTests
{
    private static readonly TimeSpan Ready = TimeSpan.FromSeconds(5);

    [Test]
    public async Task DotNet_push_to_nng_pull_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        await using PushSocket push = new();
        int port = await push.BindAsync("tcp://127.0.0.1:0");
        NativeSocket pull = NativeNng.OpenPull0();
        try
        {
            NativeNng.Dial(pull, $"tcp://127.0.0.1:{port}");
            await push.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNng.Receive(pull));
            await push.SendAsync(Bytes("hello-nng"));
            await AssertReceived(received, "hello-nng");
        }
        finally
        {
            NativeNng.Close(pull);
        }
    }

    [Test]
    public async Task Nng_push_to_dotNet_pull_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        int port = FreePort();
        NativeSocket push = NativeNng.OpenPush0();
        await using PullSocket pull = new();
        try
        {
            NativeNng.Listen(push, $"tcp://127.0.0.1:{port}");
            pull.Connect($"tcp://127.0.0.1:{port}");
            await pull.WaitForConnectionsAsync(1, Ready);

            NativeNng.Send(push, Bytes("world-nng"));
            await Assert.That(await ReceiveText(pull.ReceiveAsync)).IsEqualTo("world-nng");
        }
        finally
        {
            NativeNng.Close(push);
        }
    }

    [Test]
    public async Task DotNet_pub_to_nng_sub_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        await using PublishSocket pub = new();
        int port = await pub.BindAsync("tcp://127.0.0.1:0");
        NativeSocket sub = NativeNng.OpenSub0();
        try
        {
            NativeNng.Subscribe(sub, Bytes("topic"));
            NativeNng.Dial(sub, $"tcp://127.0.0.1:{port}");
            await pub.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNng.Receive(sub));
            for (int i = 0; i < 20 && !received.IsCompleted; i++)
            {
                await pub.SendAsync(Bytes("topic:data"));
                await Task.Delay(50);
            }

            await AssertReceived(received, "topic:data");
        }
        finally
        {
            NativeNng.Close(sub);
        }
    }

    [Test]
    public async Task Nng_pub_to_dotNet_sub_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        int port = FreePort();
        NativeSocket pub = NativeNng.OpenPub0();
        await using SubscribeSocket sub = new();
        try
        {
            sub.Subscribe("topic");
            NativeNng.Listen(pub, $"tcp://127.0.0.1:{port}");
            sub.Connect($"tcp://127.0.0.1:{port}");
            await sub.WaitForConnectionsAsync(1, Ready);

            using CancellationTokenSource cts = new(Ready);
            ValueTask<NanoMessage> pending = sub.ReceiveAsync(cts.Token);
            for (int i = 0; i < 20 && !pending.IsCompleted; i++)
            {
                NativeNng.Send(pub, Bytes("topic:push"));
                await Task.Delay(50);
            }

            using NanoMessage message = await pending;
            await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("topic:push");
        }
        finally
        {
            NativeNng.Close(pub);
        }
    }

    [Test]
    public async Task DotNet_req_to_nng_rep_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        await using RequestSocket req = new();
        int port = await req.BindAsync("tcp://127.0.0.1:0");
        NativeSocket rep = NativeNng.OpenRep0();
        try
        {
            NativeNng.Dial(rep, $"tcp://127.0.0.1:{port}");
            await req.WaitForConnectionsAsync(1, Ready);

            Task responder = Task.Run(() =>
            {
                byte[]? request = NativeNng.Receive(rep);
                if (request is not null)
                {
                    NativeNng.Send(rep, Bytes("nng-reply"));
                }
            });

            using CancellationTokenSource cts = new(Ready);
            using NanoMessage reply = await req.RequestAsync(Bytes("nng-request"), cts.Token);
            await Assert.That(Encoding.ASCII.GetString(reply.Span)).IsEqualTo("nng-reply");
            await responder;
        }
        finally
        {
            NativeNng.Close(rep);
        }
    }

    [Test]
    public async Task Nng_req_to_dotNet_rep_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        int port = FreePort();
        NativeSocket req = NativeNng.OpenReq0();
        await using ReplySocket rep = new();
        try
        {
            NativeNng.Listen(req, $"tcp://127.0.0.1:{port}");
            rep.Connect($"tcp://127.0.0.1:{port}");
            await rep.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> roundTrip = Task.Run(() =>
            {
                NativeNng.Send(req, Bytes("ask"));
                return NativeNng.Receive(req);
            });

            using CancellationTokenSource cts = new(Ready);
            using (NanoMessage request = await rep.ReceiveAsync(cts.Token))
            {
                await Assert.That(Encoding.ASCII.GetString(request.Span)).IsEqualTo("ask");
            }

            await rep.ReplyAsync(Bytes("answered"), cts.Token);
            await AssertReceived(roundTrip, "answered");
        }
        finally
        {
            NativeNng.Close(req);
        }
    }

    [Test]
    public async Task DotNet_pair0_to_nng_pair0_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        await using PairSocket pair = new();
        int port = await pair.BindAsync("tcp://127.0.0.1:0");
        NativeSocket native = NativeNng.OpenPair0();
        try
        {
            NativeNng.Dial(native, $"tcp://127.0.0.1:{port}");
            await pair.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNng.Receive(native));
            await pair.SendAsync(Bytes("to-nng"));
            await AssertReceived(received, "to-nng");

            NativeNng.Send(native, Bytes("to-dotnet"));
            await Assert.That(await ReceiveText(pair.ReceiveAsync)).IsEqualTo("to-dotnet");
        }
        finally
        {
            NativeNng.Close(native);
        }
    }

    [Test]
    public async Task DotNet_pair1_to_nng_pair1_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        await using Pair1Socket pair = new();
        int port = await pair.BindAsync("tcp://127.0.0.1:0");
        NativeSocket native = NativeNng.OpenPair1();
        try
        {
            NativeNng.Dial(native, $"tcp://127.0.0.1:{port}");
            await pair.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNng.Receive(native));
            await pair.SendAsync(Bytes("v1-to-nng"));
            await AssertReceived(received, "v1-to-nng");

            NativeNng.Send(native, Bytes("v1-to-dotnet"));
            using CancellationTokenSource cts = new(Ready);
            using NanoMessage message = await pair.ReceiveAsync(cts.Token);
            await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("v1-to-dotnet");
        }
        finally
        {
            NativeNng.Close(native);
        }
    }

    [Test]
    public async Task DotNet_bus_to_nng_bus_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        await using BusSocket bus = new();
        int port = await bus.BindAsync("tcp://127.0.0.1:0");
        NativeSocket native = NativeNng.OpenBus0();
        try
        {
            NativeNng.Dial(native, $"tcp://127.0.0.1:{port}");
            await bus.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNng.Receive(native));
            for (int i = 0; i < 20 && !received.IsCompleted; i++)
            {
                await bus.SendAsync(Bytes("bus-nng"));
                await Task.Delay(50);
            }

            await AssertReceived(received, "bus-nng");
        }
        finally
        {
            NativeNng.Close(native);
        }
    }

    [Test]
    public async Task DotNet_surveyor_to_nng_respondent_over_tcp()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        NanoSocketOptions options = new() { SurveyDeadline = TimeSpan.FromSeconds(2) };
        await using SurveyorSocket surveyor = new(options);
        int port = await surveyor.BindAsync("tcp://127.0.0.1:0");
        NativeSocket respondent = NativeNng.OpenRespondent0();
        try
        {
            NativeNng.Dial(respondent, $"tcp://127.0.0.1:{port}");
            await surveyor.WaitForConnectionsAsync(1, Ready);

            Task responder = Task.Run(() =>
            {
                byte[]? survey = NativeNng.Receive(respondent);
                if (survey is not null)
                {
                    NativeNng.Send(respondent, Bytes("nng-answer"));
                }
            });

            using CancellationTokenSource cts = new(Ready);
            IReadOnlyList<NanoMessage> responses = await surveyor.SurveyAsync(Bytes("nng-question"), cts.Token);
            try
            {
                bool gotAnswer = responses.Any(r => Encoding.ASCII.GetString(r.Span) == "nng-answer");
                await Assert.That(gotAnswer).IsTrue();
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
        finally
        {
            NativeNng.Close(respondent);
        }
    }

    [Test]
    public async Task DotNet_push_to_nng_pull_over_ipc()
    {
        if (!NativeNng.IsAvailable || OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), $"nng-{Guid.NewGuid():N}.sock");
        await using PushSocket push = new();
        await push.BindAsync($"ipc://{path}");
        NativeSocket pull = NativeNng.OpenPull0();
        try
        {
            NativeNng.Dial(pull, $"ipc://{path}");
            await push.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNng.Receive(pull));
            await push.SendAsync(Bytes("ipc-nng"));
            await AssertReceived(received, "ipc-nng");
        }
        finally
        {
            NativeNng.Close(pull);
            TryDelete(path);
        }
    }

    [Test]
    public async Task DotNet_push_to_nng_pull_over_ws()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        await using PushSocket push = new();
        int port = await push.BindAsync("ws://127.0.0.1:0");
        NativeSocket pull = NativeNng.OpenPull0();
        try
        {
            NativeNng.Dial(pull, $"ws://127.0.0.1:{port}/");
            await push.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNng.Receive(pull));
            await push.SendAsync(Bytes("ws-nng"));
            await AssertReceived(received, "ws-nng");
        }
        finally
        {
            NativeNng.Close(pull);
        }
    }

    [Test]
    public async Task Nng_push_to_dotNet_pull_over_ws()
    {
        if (!NativeNng.IsAvailable)
        {
            return;
        }

        int port = FreePort();
        NativeSocket push = NativeNng.OpenPush0();
        await using PullSocket pull = new();
        try
        {
            NativeNng.Listen(push, $"ws://127.0.0.1:{port}/");
            pull.Connect($"ws://127.0.0.1:{port}/");
            await pull.WaitForConnectionsAsync(1, Ready);

            NativeNng.Send(push, Bytes("ws-from-nng"));
            await Assert.That(await ReceiveText(pull.ReceiveAsync)).IsEqualTo("ws-from-nng");
        }
        finally
        {
            NativeNng.Close(push);
        }
    }

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private static int FreePort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static async Task AssertReceived(Task<byte[]?> received, string expected)
    {
        byte[]? bytes = await received;
        await Assert.That(bytes is not null).IsTrue();
        await Assert.That(Encoding.ASCII.GetString(bytes!)).IsEqualTo(expected);
    }

    private static async Task<string> ReceiveText(Func<CancellationToken, ValueTask<NanoMessage>> receive)
    {
        using CancellationTokenSource cts = new(Ready);
        using NanoMessage message = await receive(cts.Token);
        return Encoding.ASCII.GetString(message.Span);
    }
}
