// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Channels;
using NanoMsg.Transports;
using NanoMsg.Wire;
#if NETSTANDARD2_0 || NETSTANDARD2_1
using PipeSignal = System.Threading.Tasks.TaskCompletionSource<bool>;
#else
using PipeSignal = System.Threading.Tasks.TaskCompletionSource;
#endif

namespace NanoMsg.Protocols;

/// <summary>
/// The transport-agnostic engine shared by every scalability protocol. It owns endpoint lifecycle
/// (bind/accept and connect/reconnect), the set of connected <see cref="NanoPipe"/> peers, a per-pipe
/// read loop that slices length-prefixed frames out of the <see cref="PipeReader"/> buffer and, at the
/// ownership boundary, copies deliverable payloads into a fair-queued inbound channel. Protocol
/// subclasses choose the send routing (broadcast, round-robin, single) and the receive policy.
/// </summary>
internal abstract class NanoSocketCore : IAsyncDisposable
{
    private readonly SpProtocol _localProtocol;
    private readonly NanoSocketOptions _options;
    private readonly object _gate = new();
    private readonly List<NanoPipe> _pipes = [];
    private readonly List<INanoListener> _listeners = [];
    private readonly List<Task> _loops = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<InboundMessage> _inbound = Channel.CreateUnbounded<InboundMessage>();
    private PipeSignal _pipeAdded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _roundRobin;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="NanoSocketCore"/> class.</summary>
    /// <param name="localProtocol">The protocol this socket advertises in the SP handshake.</param>
    /// <param name="options">Tuning options; defaults are used when <see langword="null"/>.</param>
    protected NanoSocketCore(SpProtocol localProtocol, NanoSocketOptions? options)
    {
        _localProtocol = localProtocol;
        _options = options ?? new NanoSocketOptions();
    }

    /// <summary>Gets a value indicating whether inbound messages are queued for the application.</summary>
    protected virtual bool DeliversInbound => true;

    /// <summary>Gets the active options.</summary>
    protected NanoSocketOptions Options => _options;

    /// <summary>Binds a local endpoint and begins accepting peers.</summary>
    /// <param name="address">The endpoint address (for example <c>tcp://*:5555</c>).</param>
    /// <param name="cancellationToken">A token used to cancel the bind.</param>
    /// <returns>The resolved local port for <c>tcp</c> endpoints (0 for other transports).</returns>
    public async ValueTask<int> BindAsync(string address, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        NanoAddress parsed = NanoAddress.Parse(address);
        INanoTransport transport = TransportFactory.For(parsed.Scheme);
        INanoListener listener = await transport.BindAsync(parsed, _options, _localProtocol, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _listeners.Add(listener);
            _loops.Add(AcceptLoopAsync(listener));
        }

        return listener.Port;
    }

    /// <summary>Begins connecting (and reconnecting) to a remote endpoint in the background.</summary>
    /// <param name="address">The endpoint address.</param>
    public void Connect(string address)
    {
        ThrowIfDisposed();
        NanoAddress parsed = NanoAddress.Parse(address);
        INanoTransport transport = TransportFactory.For(parsed.Scheme);
        lock (_gate)
        {
            _loops.Add(ConnectLoopAsync(transport, parsed));
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        INanoListener[] listeners;
        NanoPipe[] pipes;
        Task[] loops;
        lock (_gate)
        {
            listeners = [.. _listeners];
            pipes = [.. _pipes];
            loops = [.. _loops];
        }

        foreach (INanoListener listener in listeners)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        foreach (NanoPipe pipe in pipes)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: the loops observe the shutdown token.
        }

        _inbound.Writer.TryComplete();
        while (_inbound.Reader.TryRead(out InboundMessage leftover))
        {
            leftover.Message.Dispose();
        }

        _shutdown.Dispose();
    }

    /// <summary>Sends <paramref name="body"/> to every connected peer (publish/bus fan-out).</summary>
    protected async ValueTask BroadcastAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        NanoPipe[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _pipes];
        }

        foreach (NanoPipe pipe in snapshot)
        {
            try
            {
                await pipe.SendAsync(body, cancellationToken).ConfigureAwait(false);
            }
            catch (NanoMsgException)
            {
                // The peer is closing; its read loop will remove it. Skip and continue the fan-out.
            }
        }
    }

    /// <summary>Broadcasts a 4-byte header + body to every peer (used by the surveyor).</summary>
    protected async ValueTask BroadcastAsync(
        uint header,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        NanoPipe[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _pipes];
        }

        foreach (NanoPipe pipe in snapshot)
        {
            try
            {
                await pipe.SendAsync(header, body, cancellationToken).ConfigureAwait(false);
            }
            catch (NanoMsgException)
            {
            }
        }
    }

    /// <summary>Best-effort send of a 4-byte header + body to a single pipe (used by replies).</summary>
    protected static async ValueTask TrySendToPipeAsync(
        NanoPipe pipe,
        uint header,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        try
        {
            await pipe.SendAsync(header, body, cancellationToken).ConfigureAwait(false);
        }
        catch (NanoMsgException)
        {
            // The requester's pipe is gone; it will retry against another peer.
        }
    }

    /// <summary>Sends <paramref name="body"/> to the next peer in round-robin order (load balancing).</summary>
    protected async ValueTask SendRoundRobinAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        while (true)
        {
            NanoPipe pipe = await AcquirePipeAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await pipe.SendAsync(body, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (NanoMsgException)
            {
            }
        }
    }

    /// <summary>Round-robin send of a 4-byte <paramref name="header"/> + <paramref name="body"/> (request).</summary>
    protected async ValueTask SendRoundRobinAsync(
        uint header,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        while (true)
        {
            NanoPipe pipe = await AcquirePipeAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await pipe.SendAsync(header, body, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (NanoMsgException)
            {
            }
        }
    }

    private async ValueTask<NanoPipe> AcquirePipeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            NanoPipe? pipe = TryGetNextPipe();
            if (pipe is not null)
            {
                return pipe;
            }

            Task added = Volatile.Read(ref _pipeAdded).Task;
            if (HasPipes())
            {
                continue;
            }

            await added.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Receives the next inbound message; the caller owns and must dispose the payload.</summary>
    protected async ValueTask<NanoMessage> ReceivePayloadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        InboundMessage inbound = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return inbound.Message;
    }

    /// <summary>Receives the next inbound message along with its source pipe (used by reply routing).</summary>
    protected async ValueTask<InboundMessage> ReceiveInboundAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until at least <paramref name="minCount"/> peers are connected.</summary>
    /// <param name="minCount">The minimum number of peers to wait for.</param>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <param name="cancellationToken">A token used to cancel the wait.</param>
    internal async ValueTask WaitForPipesAsync(int minCount, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (true)
        {
            Task added = Volatile.Read(ref _pipeAdded).Task;
            lock (_gate)
            {
                if (_pipes.Count >= minCount)
                {
                    return;
                }
            }

            await added.WaitAsync(cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Determines whether a received message should be delivered to the application.</summary>
    /// <param name="pipe">The pipe the message arrived on.</param>
    /// <param name="body">The message body, sliced over the pipe buffer.</param>
    protected virtual bool ShouldDeliver(NanoPipe pipe, in ReadOnlySequence<byte> body) => true;

    /// <summary>Determines whether <paramref name="prefix"/> is a prefix of <paramref name="sequence"/>.</summary>
    protected static bool StartsWith(in ReadOnlySequence<byte> sequence, ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty)
        {
            return true;
        }

        if (sequence.Length < prefix.Length)
        {
            return false;
        }

        ReadOnlySequence<byte> head = sequence.Slice(0, prefix.Length);
        if (head.IsSingleSegment)
        {
#if NETSTANDARD2_0
            return head.First.Span.SequenceEqual(prefix);
#else
            return head.FirstSpan.SequenceEqual(prefix);
#endif
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(prefix.Length);
        head.CopyTo(rented);
        bool match = rented.AsSpan(0, prefix.Length).SequenceEqual(prefix);
        ArrayPool<byte>.Shared.Return(rented);
        return match;
    }

    private NanoPipe? TryGetNextPipe()
    {
        lock (_gate)
        {
            if (_pipes.Count == 0)
            {
                return null;
            }

            int index = (int)(_roundRobin++ % (uint)_pipes.Count);
            return _pipes[index];
        }
    }

    private bool HasPipes()
    {
        lock (_gate)
        {
            return _pipes.Count > 0;
        }
    }

    private async Task AcceptLoopAsync(INanoListener listener)
    {
        CancellationToken cancellationToken = _shutdown.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                INanoConnection connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                _ = EstablishAcceptedAsync(connection, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (NanoMsgException)
        {
        }
    }

    private async Task EstablishAcceptedAsync(INanoConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            SpProtocol peer = await SpHandshake.PerformAsync(connection, _localProtocol, cancellationToken)
                .ConfigureAwait(false);
            NanoPipe pipe = new(connection, peer);
            AddPipe(pipe);
            await ReadLoopAsync(pipe).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NanoMsgException or IOException or OperationCanceledException
            or ObjectDisposedException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ConnectLoopAsync(INanoTransport transport, NanoAddress address)
    {
        CancellationToken cancellationToken = _shutdown.Token;
        TimeSpan delay = _options.ReconnectInterval;
        while (!cancellationToken.IsCancellationRequested)
        {
            INanoConnection? connection = null;
            try
            {
                connection = await transport.ConnectAsync(address, _options, _localProtocol, cancellationToken)
                    .ConfigureAwait(false);
                SpProtocol peer = await SpHandshake.PerformAsync(connection, _localProtocol, cancellationToken)
                    .ConfigureAwait(false);
                NanoPipe pipe = new(connection, peer);
                AddPipe(pipe);
                delay = _options.ReconnectInterval;
                await ReadLoopAsync(pipe).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NanoMsgException or IOException or OperationCanceledException
                or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_options.ReconnectIntervalMax > TimeSpan.Zero)
            {
                long doubled = Math.Min(delay.Ticks * 2, _options.ReconnectIntervalMax.Ticks);
                delay = TimeSpan.FromTicks(doubled);
            }
        }
    }

    private async Task ReadLoopAsync(NanoPipe pipe)
    {
        CancellationToken cancellationToken = _shutdown.Token;
        PipeReader reader = pipe.Input;
        long maxBody = _options.ReceiveMaxMessageSize < 0 ? long.MaxValue : _options.ReceiveMaxMessageSize;
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition consumed = buffer.Start;

                while (SpFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> body, maxBody))
                {
                    Dispatch(pipe, body);
                    consumed = buffer.Start;
                }

                reader.AdvanceTo(consumed, buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is NanoMsgException or IOException or OperationCanceledException
            or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
        }
        finally
        {
            RemovePipe(pipe);
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Dispatch(NanoPipe pipe, in ReadOnlySequence<byte> frame) => OnFrame(pipe, frame);

    /// <summary>Processes a complete inbound frame. The default queues it for fair-queued receive.</summary>
    /// <param name="pipe">The pipe the frame arrived on.</param>
    /// <param name="frame">The frame body, sliced over the pipe buffer.</param>
    protected virtual void OnFrame(NanoPipe pipe, in ReadOnlySequence<byte> frame)
    {
        if (!DeliversInbound || !ShouldDeliver(pipe, frame))
        {
            return;
        }

        EnqueueInbound(new InboundMessage(pipe, NanoMessage.CopyFrom(frame)));
    }

    /// <summary>Queues an inbound message for delivery; disposes it if the queue is already closed.</summary>
    /// <param name="inbound">The message to enqueue.</param>
    protected void EnqueueInbound(InboundMessage inbound)
    {
        if (!_inbound.Writer.TryWrite(inbound))
        {
            inbound.Message.Dispose();
        }
    }

    /// <summary>Reads a 4-byte big-endian header and the trailing payload from a frame.</summary>
    /// <param name="frame">The frame body.</param>
    /// <param name="header">The decoded 4-byte header.</param>
    /// <param name="payload">The payload following the header.</param>
    /// <returns><see langword="true"/> if the frame was long enough to contain a header.</returns>
    protected static bool TryReadHeader(
        in ReadOnlySequence<byte> frame,
        out uint header,
        out ReadOnlySequence<byte> payload)
    {
        header = 0;
        payload = default;
        if (frame.Length < SpFraming.HeaderSize)
        {
            return false;
        }

        Span<byte> id = stackalloc byte[SpFraming.HeaderSize];
        frame.Slice(0, SpFraming.HeaderSize).CopyTo(id);
        header = BinaryPrimitives.ReadUInt32BigEndian(id);
        payload = frame.Slice(SpFraming.HeaderSize);
        return true;
    }

    private void AddPipe(NanoPipe pipe)
    {
        lock (_gate)
        {
            _pipes.Add(pipe);
        }

        PipeSignal previous = Interlocked.Exchange(
            ref _pipeAdded,
            new PipeSignal(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    private void RemovePipe(NanoPipe pipe)
    {
        lock (_gate)
        {
            _pipes.Remove(pipe);
        }
    }

    private void ThrowIfDisposed()
    {
#if NETSTANDARD2_0 || NETSTANDARD2_1
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
#else
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
#endif
    }

    /// <summary>Throws if the socket has been disposed.</summary>
    private protected void ThrowIfDisposedCore() => ThrowIfDisposed();
}
