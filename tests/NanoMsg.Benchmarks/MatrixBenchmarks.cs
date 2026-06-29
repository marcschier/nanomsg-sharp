// Copyright (c) marcschier. Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using BenchmarkDotNet.Attributes;

namespace NanoMsg.Benchmarks;

/// <summary>
/// The protocol × transport × size throughput matrix: for each scalability protocol over each transport
/// at a given message size, how many operations/sec? One-way protocols count one op per delivered
/// message; round-trip protocols (REQ/REP, SURVEY) count one op per round trip. Each combination moves
/// <see cref="MessageCount"/> messages, so BenchmarkDotNet's mean is per-op and the <c>Ops/sec</c> column
/// reads as 1e9 / mean(ns). Filter a subset with, e.g., <c>--filter '*Tcp*16384*'</c> or by category
/// (<c>--anyCategories quic reqrep</c>).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BenchConfig))]
public class MatrixBenchmarks
{
    private const int MessageCount = 500;
    private static readonly TimeSpan Ready = TimeSpan.FromSeconds(20);

    private MessagingPair _pair = null!;
    private byte[] _payload = [];
    private X509Certificate2? _cert;

    /// <summary>Gets or sets the protocol under test.</summary>
    [ParamsAllValues]
    public BenchProtocol Protocol { get; set; }

    /// <summary>Gets or sets the transport under test.</summary>
    [ParamsAllValues]
    public BenchTransport Transport { get; set; }

    /// <summary>Gets or sets the payload size in bytes (valid for every transport, including datagram).</summary>
    [Params(64, 1024, 16384)]
    public int Size { get; set; }

    /// <summary>Builds and connects the protocol/transport pair.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        if (!BenchTransports.IsSupported(Transport))
        {
            throw new InvalidOperationException($"Transport {Transport} is not available on this machine.");
        }

        BenchTransports.EnsureDtls();
        _payload = new byte[Size];
        _cert = BenchTransports.NeedsCertificate(Transport) ? BenchTransports.CreateCertificate() : null;
        _pair = await MessagingPair.CreateAsync(Protocol, Transport, _cert, Ready);
    }

    /// <summary>Moves <see cref="MessageCount"/> operations through the pair.</summary>
    [Benchmark(OperationsPerInvoke = MessageCount)]
    public Task Run() => _pair.RunAsync(_payload, MessageCount);

    /// <summary>Tears the pair down.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_pair is not null)
        {
            await _pair.DisposeAsync();
        }

        _cert?.Dispose();
    }
}

/// <summary>
/// Large-message throughput for the stream transports only (datagram transports cap a single message
/// near 65000 bytes). Same shape as <see cref="MatrixBenchmarks"/> with a stream-only transport set and
/// big sizes.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BenchConfig))]
public class LargeMessageBenchmarks
{
    private const int MessageCount = 100;
    private static readonly TimeSpan Ready = TimeSpan.FromSeconds(20);

    private MessagingPair _pair = null!;
    private byte[] _payload = [];
    private X509Certificate2? _cert;

    /// <summary>Gets or sets the protocol under test.</summary>
    [ParamsAllValues]
    public BenchProtocol Protocol { get; set; }

    /// <summary>Gets or sets the stream transport under test.</summary>
    [Params(
        BenchTransport.Inproc, BenchTransport.Tcp, BenchTransport.Ipc, BenchTransport.Ws,
        BenchTransport.TlsTcp, BenchTransport.Wss, BenchTransport.Quic)]
    public BenchTransport Transport { get; set; }

    /// <summary>Gets or sets the payload size in bytes.</summary>
    [Params(262144, 1048576)]
    public int Size { get; set; }

    /// <summary>Builds and connects the protocol/transport pair.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        if (!BenchTransports.IsSupported(Transport))
        {
            throw new InvalidOperationException($"Transport {Transport} is not available on this machine.");
        }

        _payload = new byte[Size];
        _cert = BenchTransports.NeedsCertificate(Transport) ? BenchTransports.CreateCertificate() : null;
        _pair = await MessagingPair.CreateAsync(Protocol, Transport, _cert, Ready);
    }

    /// <summary>Moves <see cref="MessageCount"/> operations through the pair.</summary>
    [Benchmark(OperationsPerInvoke = MessageCount)]
    public Task Run() => _pair.RunAsync(_payload, MessageCount);

    /// <summary>Tears the pair down.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_pair is not null)
        {
            await _pair.DisposeAsync();
        }

        _cert?.Dispose();
    }
}
