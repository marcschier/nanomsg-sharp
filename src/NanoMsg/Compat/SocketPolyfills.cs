// Copyright (c) marcschier. Licensed under the MIT License.

#if NETSTANDARD2_0 || NETSTANDARD2_1
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NanoMsg.Compat;

/// <summary>
/// <c>Socket</c> and <c>Task</c> async polyfills (Memory + <see cref="CancellationToken"/> overloads,
/// net5/net6+) for the netstandard builds. Each delegates to the netstandard <c>SocketTaskExtensions</c>
/// (zero-copy via <c>MemoryMarshal.TryGetArray</c>, since every buffer here is array-backed) and layers
/// best-effort cancellation on top (the underlying op is abandoned, not the socket). On net8/9/10 the
/// real instance methods are used and this file is not compiled.
/// </summary>
internal static class SocketPolyfills
{
#if NETSTANDARD2_0
    public static ValueTask<int> SendAsync(
        this Socket socket,
        ReadOnlyMemory<byte> buffer,
        SocketFlags flags,
        CancellationToken cancellationToken = default) =>
        new(WithCancellation(SocketTaskExtensions.SendAsync(socket, Segment(buffer), flags), cancellationToken));

    public static ValueTask<int> ReceiveAsync(
        this Socket socket,
        Memory<byte> buffer,
        SocketFlags flags,
        CancellationToken cancellationToken = default) =>
        new(WithCancellation(SocketTaskExtensions.ReceiveAsync(socket, Segment(buffer), flags), cancellationToken));
#endif

    public static ValueTask<int> SendToAsync(
        this Socket socket,
        ReadOnlyMemory<byte> buffer,
        SocketFlags flags,
        EndPoint remoteEndPoint,
        CancellationToken cancellationToken) =>
        new(WithCancellation(
            SocketTaskExtensions.SendToAsync(socket, Segment(buffer), flags, remoteEndPoint), cancellationToken));

    public static ValueTask<SocketReceiveFromResult> ReceiveFromAsync(
        this Socket socket,
        Memory<byte> buffer,
        SocketFlags flags,
        EndPoint remoteEndPoint,
        CancellationToken cancellationToken) =>
        new(WithCancellation(
            SocketTaskExtensions.ReceiveFromAsync(socket, Segment(buffer), flags, remoteEndPoint), cancellationToken));

    public static ValueTask ConnectAsync(
        this Socket socket, EndPoint remoteEndPoint, CancellationToken cancellationToken) =>
        new(WithCancellation(SocketTaskExtensions.ConnectAsync(socket, remoteEndPoint), cancellationToken));

    public static ValueTask<Socket> AcceptAsync(this Socket socket, CancellationToken cancellationToken) =>
        new(WithCancellation(SocketTaskExtensions.AcceptAsync(socket), cancellationToken));

    internal static ArraySegment<byte> Segment(ReadOnlyMemory<byte> memory) =>
        MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment)
            ? segment
            : new ArraySegment<byte>(memory.ToArray());

    internal static ArraySegment<byte> Segment(Memory<byte> memory) =>
        MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory, out ArraySegment<byte> segment)
            ? segment
            : new ArraySegment<byte>(memory.ToArray());

    internal static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelled))
        {
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return await task.ConfigureAwait(false);
    }

    internal static async Task WithCancellation(Task task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelled))
        {
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        await task.ConfigureAwait(false);
    }
}

/// <summary>Polyfill for <c>Task.WaitAsync(CancellationToken)</c> (net6+).</summary>
internal static class TaskPolyfills
{
    public static Task WaitAsync(this Task task, CancellationToken cancellationToken) =>
        SocketPolyfills.WithCancellation(task, cancellationToken);
}

/// <summary>Polyfills for static platform/host helpers missing on netstandard.</summary>
internal static class PlatformPolyfills
{
    public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken) =>
        SocketPolyfills.WithCancellation(Dns.GetHostAddressesAsync(host), cancellationToken);
}
#endif
