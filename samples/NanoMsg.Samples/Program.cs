// Copyright (c) marcschier. Licensed under the MIT License.

using System.Text;

namespace NanoMsg.Samples;

/// <summary>A short tour of the NanoMsgSharp scalability protocols over the in-process transport.</summary>
internal static class Program
{
    private static async Task Main()
    {
        await RunPublishSubscribeAsync();
        await RunRequestReplyAsync();
        await RunPushPullAsync();
        await RunPair1Async();
        await RunUdpAsync();
    }

    private static async Task RunPublishSubscribeAsync()
    {
        Console.WriteLine("== PUB/SUB ==");
        await using PublishSocket pub = new();
        await using SubscribeSocket sub = new();

        const string address = "inproc://sample-pubsub";
        await pub.BindAsync(address);
        sub.Connect(address);
        sub.Subscribe("weather");

        // PUB/SUB has a slow-joiner: give the subscriber a moment to connect before publishing.
        await Task.Delay(200);
        await pub.SendAsync("weather: sunny"u8.ToArray());
        await pub.SendAsync("sports: ignored"u8.ToArray());

        using NanoMessage message = await sub.ReceiveAsync();
        Console.WriteLine($"  subscriber received: {Encoding.UTF8.GetString(message.Span)}");
    }

    private static async Task RunRequestReplyAsync()
    {
        Console.WriteLine("== REQ/REP ==");
        await using ReplySocket rep = new();
        await using RequestSocket req = new();

        const string address = "inproc://sample-reqrep";
        await rep.BindAsync(address);
        req.Connect(address);

        Task responder = Task.Run(async () =>
        {
            using NanoMessage request = await rep.ReceiveAsync();
            string text = Encoding.UTF8.GetString(request.Span);
            await rep.ReplyAsync(Encoding.UTF8.GetBytes($"echo:{text}"));
        });

        using NanoMessage reply = await req.RequestAsync("hello"u8.ToArray());
        Console.WriteLine($"  requester received: {Encoding.UTF8.GetString(reply.Span)}");
        await responder;
    }

    private static async Task RunPushPullAsync()
    {
        Console.WriteLine("== PUSH/PULL ==");
        await using PushSocket push = new();
        await using PullSocket pull = new();

        const string address = "inproc://sample-pipeline";
        await push.BindAsync(address);
        pull.Connect(address);

        for (int i = 1; i <= 3; i++)
        {
            await push.SendAsync(Encoding.UTF8.GetBytes($"task-{i}"));
        }

        for (int i = 0; i < 3; i++)
        {
            using NanoMessage work = await pull.ReceiveAsync();
            Console.WriteLine($"  worker received: {Encoding.UTF8.GetString(work.Span)}");
        }
    }

    private static async Task RunPair1Async()
    {
        // PAIR1 is NNG's versioned pair: it adds a hop-count (TTL) header and interoperates with
        // a C libnng pair1 peer. Here two NanoMsgSharp PAIR1 sockets exchange a message each way.
        Console.WriteLine("== PAIR1 (NNG) ==");
        await using Pair1Socket left = new();
        await using Pair1Socket right = new();

        const string address = "inproc://sample-pair1";
        await left.BindAsync(address);
        right.Connect(address);

        await left.SendAsync("ping"u8.ToArray());
        using (NanoMessage ping = await right.ReceiveAsync())
        {
            Console.WriteLine($"  right received: {Encoding.UTF8.GetString(ping.Span)}");
        }

        await right.SendAsync("pong"u8.ToArray());
        using NanoMessage pong = await left.ReceiveAsync();
        Console.WriteLine($"  left received: {Encoding.UTF8.GetString(pong.Span)}");
    }

    private static async Task RunUdpAsync()
    {
        // The udp:// transport maps each SP message to one UDP datagram (NNG's experimental udp model).
        // A DTLS-secured variant (dtls+udp://) is available in the NanoMsgSharp.Dtls package.
        Console.WriteLine("== UDP ==");
        await using PullSocket pull = new();
        await using PushSocket push = new();

        int port = await pull.BindAsync("udp://127.0.0.1:0");
        push.Connect($"udp://127.0.0.1:{port}");

        // Give the udp connection handshake a moment to complete before the first send.
        await Task.Delay(300);
        await push.SendAsync("datagram"u8.ToArray());

        using NanoMessage message = await pull.ReceiveAsync();
        Console.WriteLine($"  puller received: {Encoding.UTF8.GetString(message.Span)}");
    }
}
