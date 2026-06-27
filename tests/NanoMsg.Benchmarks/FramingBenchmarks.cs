// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using BenchmarkDotNet.Attributes;
using NanoMsg.Wire;

namespace NanoMsg.Benchmarks;

/// <summary>
/// Micro-benchmarks for the zero-allocation length-prefix framing primitives. With a reused buffer
/// writer and a <see cref="ReadOnlySequence{T}"/> over existing memory, both the write and read paths
/// should report 0 B allocated under the memory diagnoser.
/// </summary>
[MemoryDiagnoser]
public class FramingBenchmarks
{
    private readonly ArrayBufferWriter<byte> _writer = new(64 * 1024);
    private byte[] _payload = [];
    private byte[] _framed = [];

    /// <summary>Gets or sets the benchmarked payload size in bytes.</summary>
    [Params(0, 64, 1024, 65536)]
    public int PayloadSize { get; set; }

    /// <summary>Prepares the payload and a pre-framed buffer.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadSize];
        ArrayBufferWriter<byte> framer = new(SpFraming.LengthPrefixSize + PayloadSize);
        SpFraming.WriteFrame(framer, _payload);
        _framed = framer.WrittenMemory.ToArray();
    }

    /// <summary>Writes one length-prefixed frame into a reused buffer writer.</summary>
    /// <returns>The number of bytes written.</returns>
    [Benchmark]
    public int WriteFrame()
    {
        _writer.Clear();
        SpFraming.WriteFrame(_writer, _payload);
        return _writer.WrittenCount;
    }

    /// <summary>Reads one length-prefixed frame from a sequence over existing memory.</summary>
    /// <returns>The body length.</returns>
    [Benchmark]
    public long ReadFrame()
    {
        ReadOnlySequence<byte> buffer = new(_framed);
        SpFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> body);
        return body.Length;
    }
}
