// Copyright (c) marcschier. Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net.Sockets;
using NanoMsg.Wire;

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
        if (OperatingSystem.IsWindows())
        {
            return new ValueTask<INanoListener>(new NamedPipeListener(ToPipeName(address.Path)));
        }

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
    }

    /// <inheritdoc/>
    public async ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            NamedPipeClientStream client = new(
                ".",
                ToPipeName(address.Path),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new StreamConnection(new NonFlushingStream(client));
        }

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
[ExcludeFromCodeCoverage(Justification = "Windows-only; covered by the Windows build-test job.")]
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
