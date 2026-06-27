// Copyright (c) marcschier. Licensed under the MIT License.

using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace NanoMsg.Benchmarks;

/// <summary>
/// Minimal P/Invoke surface for the reference C <c>libnanomsg</c>, used only to produce a native
/// throughput baseline to compare against <see cref="ThroughputBenchmarks"/>. The library is present
/// only where it has been installed (Linux: <c>apt-get install libnanomsg-dev</c>).
/// </summary>
internal static partial class NativeNanoMsg
{
    private const string Library = "nanomsg";

    /// <summary>The SP address family (<c>AF_SP</c>).</summary>
    public const int ProtocolDomain = 1;

    /// <summary>The PUSH protocol number (<c>NN_PUSH</c>).</summary>
    public const int Push = 80;

    /// <summary>The PULL protocol number (<c>NN_PULL</c>).</summary>
    public const int Pull = 81;

    /// <summary>Gets a value indicating whether the native nanomsg library can be loaded.</summary>
    public static bool IsAvailable { get; } = TryProbe();

#pragma warning disable SYSLIB1054 // LibraryImport requires more ceremony; DllImport is fine for a bench helper.
    [DllImport(Library, CharSet = CharSet.Ansi)]
    public static extern int nn_socket(int domain, int protocol);

    [DllImport(Library, CharSet = CharSet.Ansi)]
    public static extern int nn_bind(int socket, string address);

    [DllImport(Library, CharSet = CharSet.Ansi)]
    public static extern int nn_connect(int socket, string address);

    [DllImport(Library)]
    public static extern int nn_send(int socket, byte[] buffer, nuint length, int flags);

    [DllImport(Library)]
    public static extern int nn_recv(int socket, byte[] buffer, nuint length, int flags);

    [DllImport(Library)]
    public static extern int nn_close(int socket);
#pragma warning restore SYSLIB1054

    private static bool TryProbe()
    {
        try
        {
            return NativeLibrary.TryLoad(Library, out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Native PUSH/PULL ping-pong throughput via <c>libnanomsg</c>, for side-by-side comparison with the
/// managed <see cref="ThroughputBenchmarks"/>. Runs only where the native library is installed; on
/// other platforms the setup fails fast with a clear message.
/// </summary>
[MemoryDiagnoser]
public class NativeThroughputBenchmarks
{
    private const int MessageCount = 2000;

    private int _push;
    private int _pull;
    private byte[] _payload = [];
    private byte[] _receive = [];

    /// <summary>Gets or sets the benchmarked payload size in bytes.</summary>
    [Params(64, 1024)]
    public int PayloadSize { get; set; }

    /// <summary>Creates and connects native PUSH/PULL sockets over inproc.</summary>
    [GlobalSetup]
    public void Setup()
    {
        if (!NativeNanoMsg.IsAvailable)
        {
            throw new InvalidOperationException(
                "libnanomsg is not installed; run this benchmark on Linux with libnanomsg-dev.");
        }

        _payload = new byte[PayloadSize];
        _receive = new byte[PayloadSize];
        string address = $"inproc://native-bench-{Guid.NewGuid():N}";
        _push = NativeNanoMsg.nn_socket(NativeNanoMsg.ProtocolDomain, NativeNanoMsg.Push);
        _pull = NativeNanoMsg.nn_socket(NativeNanoMsg.ProtocolDomain, NativeNanoMsg.Pull);
        _ = NativeNanoMsg.nn_bind(_push, address);
        _ = NativeNanoMsg.nn_connect(_pull, address);
    }

    /// <summary>Sends and receives <see cref="MessageCount"/> messages through the native sockets.</summary>
    [Benchmark(OperationsPerInvoke = MessageCount)]
    public void PushPull()
    {
        for (int i = 0; i < MessageCount; i++)
        {
            _ = NativeNanoMsg.nn_send(_push, _payload, (nuint)_payload.Length, 0);
            _ = NativeNanoMsg.nn_recv(_pull, _receive, (nuint)_receive.Length, 0);
        }
    }

    /// <summary>Closes the native sockets.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _ = NativeNanoMsg.nn_close(_push);
        _ = NativeNanoMsg.nn_close(_pull);
    }
}
