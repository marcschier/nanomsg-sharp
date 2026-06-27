// Copyright (c) marcschier. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading.Channels;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// The in-process transport. Endpoints are matched by name through a process-wide registry; a
/// connection is a pair of cross-wired <see cref="Pipe"/> instances, so messages never leave the
/// process and never touch a socket.
/// </summary>
internal sealed class InProcTransport : INanoTransport
{
    private static readonly ConcurrentDictionary<string, InProcListener> Registry =
        new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ValueTask<INanoListener> BindAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        InProcListener listener = new(address.Path, static name => Registry.TryRemove(name, out _));
        if (!Registry.TryAdd(address.Path, listener))
        {
            throw new NanoMsgException($"inproc endpoint '{address.Path}' is already bound.");
        }

        return new ValueTask<INanoListener>(listener);
    }

    /// <inheritdoc/>
    public ValueTask<INanoConnection> ConnectAsync(
        NanoAddress address,
        NanoSocketOptions options,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        if (!Registry.TryGetValue(address.Path, out InProcListener? listener))
        {
            throw new NanoMsgException($"inproc endpoint '{address.Path}' is not bound (connection refused).");
        }

        (INanoConnection client, INanoConnection server) = InProcConnection.CreatePair();
        if (!listener.TryEnqueue(server))
        {
            throw new NanoMsgException($"inproc endpoint '{address.Path}' is no longer accepting connections.");
        }

        return new ValueTask<INanoConnection>(client);
    }
}

/// <summary>An <see cref="INanoListener"/> for the in-process transport.</summary>
internal sealed class InProcListener : INanoListener
{
    private readonly Channel<INanoConnection> _pending =
        Channel.CreateUnbounded<INanoConnection>(new UnboundedChannelOptions { SingleReader = false });

    private readonly string _name;
    private readonly Action<string> _onDispose;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="InProcListener"/> class.</summary>
    /// <param name="name">The bound inproc name.</param>
    /// <param name="onDispose">A callback that removes the listener from the registry.</param>
    public InProcListener(string name, Action<string> onDispose)
    {
        _name = name;
        _onDispose = onDispose;
    }

    /// <inheritdoc/>
    public int Port => 0;

    /// <inheritdoc/>
    public ValueTask<INanoConnection> AcceptAsync(CancellationToken cancellationToken) =>
        _pending.Reader.ReadAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _onDispose(_name);
            _pending.Writer.TryComplete();
        }

        return default;
    }

    internal bool TryEnqueue(INanoConnection serverSide) => _pending.Writer.TryWrite(serverSide);
}

/// <summary>One end of an in-process connection: a reader from the peer and a writer to the peer.</summary>
internal sealed class InProcConnection : INanoConnection
{
    private int _disposed;

    private InProcConnection(PipeReader input, PipeWriter output)
    {
        Input = input;
        Output = output;
    }

    /// <inheritdoc/>
    public PipeReader Input { get; }

    /// <inheritdoc/>
    public PipeWriter Output { get; }

    /// <summary>Creates a connected client/server pair sharing two cross-wired pipes.</summary>
    /// <returns>The two ends of the connection.</returns>
    public static (INanoConnection Client, INanoConnection Server) CreatePair()
    {
        Pipe clientToServer = new();
        Pipe serverToClient = new();
        InProcConnection client = new(serverToClient.Reader, clientToServer.Writer);
        InProcConnection server = new(clientToServer.Reader, serverToClient.Writer);
        return (client, server);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Input.CompleteAsync().ConfigureAwait(false);
        await Output.CompleteAsync().ConfigureAwait(false);
    }
}
