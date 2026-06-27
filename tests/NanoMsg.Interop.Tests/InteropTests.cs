// Copyright (c) marcschier. Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NanoMsg.Interop.Tests;

/// <summary>
/// Wire-compatibility tests: a NanoMsgSharp socket on one side and a real C <c>libnanomsg</c> socket
/// (driven via <see cref="NativeNanoMsg"/>) on the other must complete each scalability-protocol
/// exchange. The tests skip when the native library is unavailable (for example on a Windows dev box
/// without <c>libnanomsg</c>), and run for real on the Linux CI runner where it is installed.
/// </summary>
public sealed class InteropTests
{
    private static readonly TimeSpan Ready = TimeSpan.FromSeconds(5);

    [Test]
    public async Task DotNet_push_to_native_pull()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        await using PushSocket push = new();
        int port = await push.BindAsync("tcp://127.0.0.1:0");
        int pull = NativeNanoMsg.CreateSocket(NativeNanoMsg.Pull);
        try
        {
            NativeNanoMsg.Connect(pull, $"tcp://127.0.0.1:{port}");
            await push.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNanoMsg.Receive(pull));
            await push.SendAsync(Bytes("hello"));
            await AssertReceived(received, "hello");
        }
        finally
        {
            NativeNanoMsg.Close(pull);
        }
    }

    [Test]
    public async Task Native_push_to_dotnet_pull()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        int port = FreePort();
        int push = NativeNanoMsg.CreateSocket(NativeNanoMsg.Push);
        await using PullSocket pull = new();
        try
        {
            NativeNanoMsg.Bind(push, $"tcp://127.0.0.1:{port}");
            pull.Connect($"tcp://127.0.0.1:{port}");
            await pull.WaitForConnectionsAsync(1, Ready);

            NativeNanoMsg.Send(push, Bytes("world"));
            await Assert.That(await ReceiveText(pull.ReceiveAsync)).IsEqualTo("world");
        }
        finally
        {
            NativeNanoMsg.Close(push);
        }
    }

    [Test]
    public async Task DotNet_push_to_native_pull_over_ipc()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        string path = $"/tmp/nano-interop-{Guid.NewGuid():N}.ipc";
        await using PushSocket push = new();
        await push.BindAsync($"ipc://{path}");
        int pull = NativeNanoMsg.CreateSocket(NativeNanoMsg.Pull);
        try
        {
            NativeNanoMsg.Connect(pull, $"ipc://{path}");
            await push.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNanoMsg.Receive(pull));
            await push.SendAsync(Bytes("ipc-hello"));
            await AssertReceived(received, "ipc-hello");
        }
        finally
        {
            NativeNanoMsg.Close(pull);
        }
    }

    [Test]
    public async Task DotNet_pub_to_native_sub()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        await using PublishSocket pub = new();
        int port = await pub.BindAsync("tcp://127.0.0.1:0");
        int sub = NativeNanoMsg.CreateSocket(NativeNanoMsg.Sub);
        try
        {
            NativeNanoMsg.Subscribe(sub, Bytes("news"));
            NativeNanoMsg.Connect(sub, $"tcp://127.0.0.1:{port}");
            await pub.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNanoMsg.Receive(sub));
            for (int i = 0; i < 20 && !received.IsCompleted; i++)
            {
                await pub.SendAsync(Bytes("news-flash"));
                await Task.Delay(50);
            }

            await AssertReceived(received, "news-flash");
        }
        finally
        {
            NativeNanoMsg.Close(sub);
        }
    }

    [Test]
    public async Task Native_pub_to_dotnet_sub()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        int port = FreePort();
        int pub = NativeNanoMsg.CreateSocket(NativeNanoMsg.Pub);
        await using SubscribeSocket sub = new();
        try
        {
            NativeNanoMsg.Bind(pub, $"tcp://127.0.0.1:{port}");
            sub.Subscribe("news");
            sub.Connect($"tcp://127.0.0.1:{port}");
            await sub.WaitForConnectionsAsync(1, Ready);

            using CancellationTokenSource cts = new(Ready);
            ValueTask<NanoMessage> receive = sub.ReceiveAsync(cts.Token);
            for (int i = 0; i < 20 && !receive.IsCompleted; i++)
            {
                NativeNanoMsg.Send(pub, Bytes("news-update"));
                await Task.Delay(50);
            }

            using NanoMessage message = await receive;
            await Assert.That(Encoding.ASCII.GetString(message.Span)).IsEqualTo("news-update");
        }
        finally
        {
            NativeNanoMsg.Close(pub);
        }
    }

    [Test]
    public async Task DotNet_request_to_native_reply()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        int port = FreePort();
        int rep = NativeNanoMsg.CreateSocket(NativeNanoMsg.Rep);
        await using RequestSocket req = new();
        try
        {
            NativeNanoMsg.Bind(rep, $"tcp://127.0.0.1:{port}");
            req.Connect($"tcp://127.0.0.1:{port}");

            Task responder = Task.Run(() =>
            {
                byte[]? request = NativeNanoMsg.Receive(rep);
                if (request is not null)
                {
                    NativeNanoMsg.Send(rep, Bytes(Encoding.ASCII.GetString(request) + "-ok"));
                }
            });

            using CancellationTokenSource cts = new(Ready);
            using NanoMessage reply = await req.RequestAsync(Bytes("ping"), cts.Token);
            await Assert.That(Encoding.ASCII.GetString(reply.Span)).IsEqualTo("ping-ok");
            await responder;
        }
        finally
        {
            NativeNanoMsg.Close(rep);
        }
    }

    [Test]
    public async Task Native_request_to_dotnet_reply()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        await using ReplySocket rep = new();
        int port = await rep.BindAsync("tcp://127.0.0.1:0");
        int req = NativeNanoMsg.CreateSocket(NativeNanoMsg.Req);
        try
        {
            NativeNanoMsg.Connect(req, $"tcp://127.0.0.1:{port}");

            using CancellationTokenSource cts = new(Ready);
            Task responder = Task.Run(async () =>
            {
                using NanoMessage request = await rep.ReceiveAsync(cts.Token);
                await rep.ReplyAsync(Bytes(Encoding.ASCII.GetString(request.Span) + "-ok"), cts.Token);
            });

            Task<byte[]?> reply = Task.Run(() =>
            {
                NativeNanoMsg.Send(req, Bytes("ping"));
                return NativeNanoMsg.Receive(req);
            });

            await responder;
            await AssertReceived(reply, "ping-ok");
        }
        finally
        {
            NativeNanoMsg.Close(req);
        }
    }

    [Test]
    public async Task DotNet_pair_to_native_pair()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        await using PairSocket pair = new();
        int port = await pair.BindAsync("tcp://127.0.0.1:0");
        int native = NativeNanoMsg.CreateSocket(NativeNanoMsg.Pair);
        try
        {
            NativeNanoMsg.Connect(native, $"tcp://127.0.0.1:{port}");
            await pair.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNanoMsg.Receive(native));
            await pair.SendAsync(Bytes("to-native"));
            await AssertReceived(received, "to-native");

            NativeNanoMsg.Send(native, Bytes("to-dotnet"));
            await Assert.That(await ReceiveText(pair.ReceiveAsync)).IsEqualTo("to-dotnet");
        }
        finally
        {
            NativeNanoMsg.Close(native);
        }
    }

    [Test]
    public async Task DotNet_bus_to_native_bus()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        await using BusSocket bus = new();
        int port = await bus.BindAsync("tcp://127.0.0.1:0");
        int native = NativeNanoMsg.CreateSocket(NativeNanoMsg.Bus);
        try
        {
            NativeNanoMsg.Connect(native, $"tcp://127.0.0.1:{port}");
            await bus.WaitForConnectionsAsync(1, Ready);

            Task<byte[]?> received = Task.Run(() => NativeNanoMsg.Receive(native));
            await bus.SendAsync(Bytes("bus-msg"));
            await AssertReceived(received, "bus-msg");
        }
        finally
        {
            NativeNanoMsg.Close(native);
        }
    }

    [Test]
    public async Task DotNet_surveyor_to_native_respondent()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            return;
        }

        NanoSocketOptions options = new() { SurveyDeadline = TimeSpan.FromSeconds(2) };
        await using SurveyorSocket surveyor = new(options);
        int port = await surveyor.BindAsync("tcp://127.0.0.1:0");
        int respondent = NativeNanoMsg.CreateSocket(NativeNanoMsg.Respondent);
        try
        {
            NativeNanoMsg.Connect(respondent, $"tcp://127.0.0.1:{port}");
            await surveyor.WaitForConnectionsAsync(1, Ready);

            Task responder = Task.Run(() =>
            {
                byte[]? survey = NativeNanoMsg.Receive(respondent);
                if (survey is not null)
                {
                    NativeNanoMsg.Send(respondent, Bytes("answer"));
                }
            });

            using CancellationTokenSource cts = new(Ready);
            IReadOnlyList<NanoMessage> responses = await surveyor.SurveyAsync(Bytes("question"), cts.Token);
            try
            {
                bool gotAnswer = responses.Any(r => Encoding.ASCII.GetString(r.Span) == "answer");
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
            NativeNanoMsg.Close(respondent);
        }
    }

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private static int FreePort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
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
