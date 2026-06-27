// Copyright (c) marcschier. Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using NanoMsg.Wire;
#if !NETSTANDARD2_0
using System.Net.Sockets;
#endif

namespace NanoMsg.Transports;

/// <summary>
/// The inter-process transport. On Unix it uses a Unix-domain socket at the address path; on Windows
/// it uses a named pipe (<c>\\.\pipe\&lt;name&gt;</c>), matching the reference nanomsg behaviour.
/// </summary>
internal sealed class IpcTransport : INanoTransport
{
    private const int Backlog = 128;

    /// <inheritdoc/>
    public ValueTask<INanoListener> BindAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0 || NETSTANDARD2_1
        if (PlatformPolyfills.IsWindows())
#else
        if (OperatingSystem.IsWindows())
#endif
        {
            return new ValueTask<INanoListener>(new NamedPipeListener(ToPipeName(address.Path)));
        }

#if NETSTANDARD2_0
        throw new PlatformNotSupportedException(
            "Unix-domain socket IPC requires netstandard2.1 or a modern .NET runtime; " +
            "use a Windows named pipe or another transport on netstandard2.0.");
#else
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            TryDeleteStaleSocketFile(address.Path);
            socket.Bind(new UnixDomainSocketEndPoint(address.Path));
            socket.Listen(Backlog);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new NanoMsgException($"Failed to bind '{address.Original}': {ex.Message}", ex);
        }

        return new ValueTask<INanoListener>(new SocketListener(socket));
#endif
    }

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0 || NETSTANDARD2_1
        if (PlatformPolyfills.IsWindows())
#else
        if (OperatingSystem.IsWindows())
#endif
        {
            NamedPipeClientStream client = new(
                ".",
                ToPipeName(address.Path),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new StreamConnection(new NonFlushingStream(client));
        }

#if NETSTANDARD2_0
        throw new PlatformNotSupportedException(
            "Unix-domain socket IPC requires netstandard2.1 or a modern .NET runtime; " +
            "use a Windows named pipe or another transport on netstandard2.0.");
#else
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(address.Path), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new NanoMsgException($"Failed to connect '{address.Original}': {ex.Message}", ex);
        }

        return new StreamConnection(new NetworkStream(socket, ownsSocket: true));
#endif
    }

    private static string ToPipeName(string path) => path.TrimStart('/', '\\');

    private static void TryDeleteStaleSocketFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Leave a live socket file in place; the subsequent Bind reports the real error.
        }
        catch (UnauthorizedAccessException)
        {
            // As above: surface the failure from Bind rather than here.
        }
    }
}

/// <summary>An <see cref="INanoListener"/> backed by Windows named pipes (one server instance per accept).</summary>
#if NET5_0_OR_GREATER
[ExcludeFromCodeCoverage(Justification = "Windows-only; covered by the Windows build-test job.")]
#else
[ExcludeFromCodeCoverage]
#endif
internal sealed class NamedPipeListener : INanoListener
{
    private const int PipeBufferSize = 64 * 1024;

    private readonly string _pipeName;

    /// <summary>Initializes a new instance of the <see cref="NamedPipeListener"/> class.</summary>
    /// <param name="pipeName">The pipe name (without the <c>\\.\pipe\</c> prefix).</param>
    public NamedPipeListener(string pipeName) => _pipeName = pipeName;

    /// <inheritdoc/>
    public int Port => 0;

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream server = new(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: PipeBufferSize,
            outBufferSize: PipeBufferSize);

        bool connected = false;
        try
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            connected = true;
            return new StreamConnection(new NonFlushingStream(server));
        }
        finally
        {
            if (!connected)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => default;
}
