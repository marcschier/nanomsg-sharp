// Copyright (c) marcschier. Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;

namespace NanoMsg.Benchmarks;

/// <summary>
/// Builds a connected pair of sockets for a protocol over a transport and runs a fixed number of
/// operations: one-way protocols stream messages from client to server; round-trip protocols
/// (REQ/REP, SURVEY) drive a background responder and count full round trips.
/// </summary>
internal sealed class MessagingPair : IAsyncDisposable
{
    private readonly Func<byte[], int, Task> _run;
    private readonly Func<ValueTask> _dispose;

    private MessagingPair(Func<byte[], int, Task> run, Func<ValueTask> dispose)
    {
        _run = run;
        _dispose = dispose;
    }

    /// <summary>Runs <paramref name="count"/> operations with the given payload.</summary>
    public Task RunAsync(byte[] payload, int count) => _run(payload, count);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _dispose();

    /// <summary>Builds and connects a pair for <paramref name="protocol"/> over <paramref name="transport"/>.</summary>
    public static Task<MessagingPair> CreateAsync(
        BenchProtocol protocol, BenchTransport transport, X509Certificate2? cert, TimeSpan ready) => protocol switch
        {
            BenchProtocol.PushPull => OneWayAsync(
                transport, cert, ready, () => new PullSocket(BenchTransports.ServerOptions(transport, cert!)),
                o => new PushSocket(o),
                (s, p, c) => ((PushSocket)s).SendAsync(p, c), (s, c) => ((PullSocket)s).ReceiveAsync(c)),
            BenchProtocol.Pair => OneWayAsync(
                transport, cert, ready, () => new PairSocket(BenchTransports.ServerOptions(transport, cert!)),
                o => new PairSocket(o),
                (s, p, c) => ((PairSocket)s).SendAsync(p, c), (s, c) => ((PairSocket)s).ReceiveAsync(c)),
            BenchProtocol.Pair1 => OneWayAsync(
                transport, cert, ready, () => new Pair1Socket(BenchTransports.ServerOptions(transport, cert!)),
                o => new Pair1Socket(o),
                (s, p, c) => ((Pair1Socket)s).SendAsync(p, c), (s, c) => ((Pair1Socket)s).ReceiveAsync(c)),
            BenchProtocol.Bus => OneWayAsync(
                transport, cert, ready, () => new BusSocket(BenchTransports.ServerOptions(transport, cert!)),
                o => new BusSocket(o),
                (s, p, c) => ((BusSocket)s).SendAsync(p, c), (s, c) => ((BusSocket)s).ReceiveAsync(c)),
            BenchProtocol.PubSub => PubSubAsync(transport, cert, ready),
            BenchProtocol.ReqRep => ReqRepAsync(transport, cert, ready),
            BenchProtocol.Survey => SurveyAsync(transport, cert, ready),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };

    private static async Task<MessagingPair> OneWayAsync(
        BenchTransport t, X509Certificate2? cert, TimeSpan ready,
        Func<NanoSocket> server, Func<NanoSocketOptions, NanoSocket> client,
        Func<NanoSocket, byte[], CancellationToken, ValueTask> send,
        Func<NanoSocket, CancellationToken, ValueTask<NanoMessage>> receive)
    {
        string addr = BenchTransports.Address(t);
        NanoSocket recv = server();
        NanoSocket snd = client(BenchTransports.ClientOptions(t));
        int port = await recv.BindAsync(addr);
        snd.Connect(BenchTransports.Connect(t, addr, port));
        await snd.WaitForConnectionsAsync(1, ready);

        Task Run(byte[] payload, int count) => Task.Run(async () =>
        {
            Task receiver = Task.Run(async () =>
            {
                for (int i = 0; i < count; i++)
                {
                    using NanoMessage m = await receive(recv, default);
                }
            });
            for (int i = 0; i < count; i++)
            {
                await send(snd, payload, default);
            }

            await receiver;
        });

        return new MessagingPair(Run, async () =>
        {
            await snd.DisposeAsync();
            await recv.DisposeAsync();
        });
    }

    private static async Task<MessagingPair> PubSubAsync(BenchTransport t, X509Certificate2? cert, TimeSpan ready)
    {
        string addr = BenchTransports.Address(t);
        PublishSocket pub = new(BenchTransports.ServerOptions(t, cert!));
        SubscribeSocket sub = new(BenchTransports.ClientOptions(t));
        int port = await pub.BindAsync(addr);
        sub.Connect(BenchTransports.Connect(t, addr, port));
        sub.Subscribe(ReadOnlySpan<byte>.Empty);
        await pub.WaitForConnectionsAsync(1, ready);
        await Task.Delay(200);

        Task Run(byte[] payload, int count) => Task.Run(async () =>
        {
            Task receiver = Task.Run(async () =>
            {
                for (int i = 0; i < count; i++)
                {
                    using NanoMessage m = await sub.ReceiveAsync();
                }
            });
            for (int i = 0; i < count; i++)
            {
                await pub.SendAsync(payload);
            }

            await receiver;
        });

        return new MessagingPair(Run, async () =>
        {
            await pub.DisposeAsync();
            await sub.DisposeAsync();
        });
    }

    private static async Task<MessagingPair> ReqRepAsync(BenchTransport t, X509Certificate2? cert, TimeSpan ready)
    {
        string addr = BenchTransports.Address(t);
        ReplySocket rep = new(BenchTransports.ServerOptions(t, cert!));
        RequestSocket req = new(BenchTransports.ClientOptions(t));
        int port = await rep.BindAsync(addr);
        req.Connect(BenchTransports.Connect(t, addr, port));
        await req.WaitForConnectionsAsync(1, ready);
        CancellationTokenSource cts = new();
        Task responder = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using NanoMessage r = await rep.ReceiveAsync(cts.Token);
                    await rep.ReplyAsync(r.Span.ToArray(), cts.Token);
                }
            }
            catch (Exception)
            {
            }
        });

        Task Run(byte[] payload, int count) => Task.Run(async () =>
        {
            for (int i = 0; i < count; i++)
            {
                using NanoMessage reply = await req.RequestAsync(payload);
            }
        });

        return new MessagingPair(Run, async () =>
        {
            await cts.CancelAsync();
            await req.DisposeAsync();
            await rep.DisposeAsync();
            try
            {
                await responder;
            }
            catch (Exception)
            {
            }

            cts.Dispose();
        });
    }

    private static async Task<MessagingPair> SurveyAsync(BenchTransport t, X509Certificate2? cert, TimeSpan ready)
    {
        NanoSocketOptions serverOptions = BenchTransports.ServerOptions(t, cert!);
        serverOptions.SurveyDeadline = TimeSpan.FromMilliseconds(5);
        SurveyorSocket surveyor = new(serverOptions);
        RespondentSocket respondent = new(BenchTransports.ClientOptions(t));
        string addr = BenchTransports.Address(t);
        int port = await surveyor.BindAsync(addr);
        respondent.Connect(BenchTransports.Connect(t, addr, port));
        await surveyor.WaitForConnectionsAsync(1, ready);
        CancellationTokenSource cts = new();
        Task responder = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using NanoMessage s = await respondent.ReceiveAsync(cts.Token);
                    await respondent.RespondAsync(s.Span.ToArray(), cts.Token);
                }
            }
            catch (Exception)
            {
            }
        });

        Task Run(byte[] payload, int count) => Task.Run(async () =>
        {
            for (int i = 0; i < count; i++)
            {
                try
                {
                    foreach (NanoMessage m in await surveyor.SurveyAsync(payload))
                    {
                        m.Dispose();
                    }
                }
                catch (NanoMsgException)
                {
                }
            }
        });

        return new MessagingPair(Run, async () =>
        {
            await cts.CancelAsync();
            await surveyor.DisposeAsync();
            await respondent.DisposeAsync();
            try
            {
                await responder;
            }
            catch (Exception)
            {
            }

            cts.Dispose();
        });
    }
}
