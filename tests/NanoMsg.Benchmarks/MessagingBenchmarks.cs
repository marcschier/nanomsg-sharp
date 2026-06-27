// Copyright (c) marcschier. Licensed under the MIT License.

using BenchmarkDotNet.Attributes;

namespace NanoMsg.Benchmarks;

/// <summary>
/// End-to-end messaging throughput over the in-process transport (PUSH/PULL), which isolates the
/// socket, pipe, and framing machinery from any network cost. The memory diagnoser reports the
/// per-message allocation of the receive path (one pooled copy at the ownership boundary).
/// </summary>
[MemoryDiagnoser]
public class ThroughputBenchmarks
{
    private const int MessageCount = 2000;
    private static readonly TimeSpan Ready = TimeSpan.FromSeconds(5);

    private PushSocket _push = null!;
    private PullSocket _pull = null!;
    private byte[] _payload = [];

    /// <summary>Gets or sets the benchmarked payload size in bytes.</summary>
    [Params(64, 1024)]
    public int PayloadSize { get; set; }

    /// <summary>Establishes a connected PUSH/PULL pair over inproc.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _payload = new byte[PayloadSize];
        string address = $"inproc://bench-throughput-{Guid.NewGuid():N}";
        _push = new PushSocket();
        _pull = new PullSocket();
        await _push.BindAsync(address);
        _pull.Connect(address);
        await _push.WaitForConnectionsAsync(1, Ready);
    }

    /// <summary>Streams <see cref="MessageCount"/> messages from PUSH to PULL.</summary>
    [Benchmark(OperationsPerInvoke = MessageCount)]
    public async Task PushPull()
    {
        Task receiver = Task.Run(async () =>
        {
            for (int i = 0; i < MessageCount; i++)
            {
                using NanoMessage message = await _pull.ReceiveAsync();
            }
        });

        for (int i = 0; i < MessageCount; i++)
        {
            await _push.SendAsync(_payload);
        }

        await receiver;
    }

    /// <summary>Tears down the sockets.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _push.DisposeAsync();
        await _pull.DisposeAsync();
    }
}

/// <summary>Request/reply round-trip latency over the in-process transport.</summary>
[MemoryDiagnoser]
public class RequestReplyBenchmarks
{
    private RequestSocket _request = null!;
    private ReplySocket _reply = null!;
    private CancellationTokenSource _cts = null!;
    private Task _responder = null!;
    private byte[] _payload = [];

    /// <summary>Gets or sets the benchmarked payload size in bytes.</summary>
    [Params(64)]
    public int PayloadSize { get; set; }

    /// <summary>Establishes a REQ/REP pair and starts a background responder loop.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _payload = new byte[PayloadSize];
        string address = $"inproc://bench-reqrep-{Guid.NewGuid():N}";
        _reply = new ReplySocket();
        _request = new RequestSocket();
        await _reply.BindAsync(address);
        _request.Connect(address);
        _cts = new CancellationTokenSource();

        byte[] response = _payload;
        ReplySocket reply = _reply;
        CancellationToken token = _cts.Token;
        _responder = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using NanoMessage request = await reply.ReceiveAsync(token);
                    await reply.ReplyAsync(response, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Performs one request/reply round trip.</summary>
    [Benchmark]
    public async Task RoundTrip()
    {
        using NanoMessage reply = await _request.RequestAsync(_payload);
    }

    /// <summary>Tears down the sockets and the responder loop.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _cts.CancelAsync();
        await _request.DisposeAsync();
        await _reply.DisposeAsync();
        await _responder;
        _cts.Dispose();
    }
}
