// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.WebSockets;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// Adapts a message-oriented <see cref="WebSocket"/> to the byte-channel <see cref="INanoConnection"/>
/// contract, following the SP-over-WebSocket mapping (<c>sp-websocket-mapping-01</c>) used by nanomsg
/// and NNG: the SP protocol is negotiated by the WebSocket sub-protocol (not the 8-byte SP handshake),
/// and each SP message maps directly to one binary WebSocket message with no length prefix.
/// <para>
/// To keep the rest of the stack (handshake + length-prefix framing) unchanged, this connection
/// <em>synthesises</em> the SP handshake the protocol layer still performs: the outbound pump swallows
/// the locally written 8-byte header, and the inbound pump injects the peer's header (built from the
/// sub-protocol-negotiated peer protocol). Thereafter it strips the 8-byte length
/// prefix from each outbound frame (sending the body as one binary message) and restores it on each
/// inbound message.
/// </para>
/// </summary>
internal sealed class WebSocketConnection : INanoConnection
{
    private const int MaxInboundMessageSize = 16 * 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly SpProtocol _peerProtocol;
    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _sendPump;
    private readonly Task _receivePump;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="WebSocketConnection"/> class.</summary>
    /// <param name="socket">The owned, already-handshaked WebSocket.</param>
    /// <param name="peerProtocol">The peer protocol negotiated via the WebSocket sub-protocol.</param>
    public WebSocketConnection(WebSocket socket, SpProtocol peerProtocol)
    {
        _socket = socket;
        _peerProtocol = peerProtocol;
        _sendPump = Task.Run(() => SendPumpAsync(_shutdown.Token));
        _receivePump = Task.Run(() => ReceivePumpAsync(_shutdown.Token));
    }

    /// <inheritdoc/>
    public PipeReader Input => _inbound.Reader;

    /// <inheritdoc/>
    public PipeWriter Output => _outbound.Writer;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_sendPump, _receivePump).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException
            or ObjectDisposedException or InvalidOperationException)
        {
        }

        _socket.Dispose();
        _shutdown.Dispose();
    }

    private async Task SendPumpAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = _outbound.Reader;
        try
        {
            // Swallow the locally written 8-byte SP handshake header; it is never sent over WebSocket.
            await DiscardAsync(reader, SpHeader.Size, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                (bool ok, ulong length) = await ReadLengthAsync(reader, cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    break;
                }

                await StreamBodyAsync(reader, length, cancellationToken).ConfigureAwait(false);
            }

            await reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException
            or ObjectDisposedException or InvalidOperationException or NanoMsgException)
        {
            await reader.CompleteAsync(ex).ConfigureAwait(false);
        }
    }

    private async Task ReceivePumpAsync(CancellationToken cancellationToken)
    {
        PipeWriter writer = _inbound.Writer;
        try
        {
            // Inject the synthesised peer handshake so SpHandshake.PerformAsync completes locally.
            Span<byte> header = writer.GetSpan(SpHeader.Size);
            new SpHeader(_peerProtocol).WriteTo(header);
            writer.Advance(SpHeader.Size);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (await PumpInboundMessageAsync(writer, cancellationToken).ConfigureAwait(false))
            {
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException
            or ObjectDisposedException or InvalidOperationException or NanoMsgException)
        {
            await writer.CompleteAsync(ex).ConfigureAwait(false);
        }
    }

    /// <summary>Receives one whole WebSocket message and writes it as one length-prefixed frame.</summary>
    /// <returns><see langword="true"/> to continue; <see langword="false"/> once the peer closed.</returns>
    private async Task<bool> PumpInboundMessageAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int total = 0;
            while (true)
            {
                if (total == buffer.Length)
                {
                    buffer = Grow(buffer, total);
                }

                ValueWebSocketReceiveResult result =
                    await _socket.ReceiveAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return false;
                }

                total += result.Count;
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            Span<byte> prefix = writer.GetSpan(SpFraming.LengthPrefixSize);
            BinaryPrimitives.WriteUInt64BigEndian(prefix, (ulong)total);
            writer.Advance(SpFraming.LengthPrefixSize);
            writer.Write(buffer.AsSpan(0, total));
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] Grow(byte[] buffer, int length)
    {
        if (buffer.Length >= MaxInboundMessageSize)
        {
            throw new NanoMsgException(
                $"Inbound WebSocket message exceeds the {MaxInboundMessageSize}-byte transport limit.");
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, MaxInboundMessageSize));
        Array.Copy(buffer, bigger, length);
        ArrayPool<byte>.Shared.Return(buffer);
        return bigger;
    }

    private static async ValueTask DiscardAsync(PipeReader reader, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.Length >= count)
            {
                reader.AdvanceTo(buffer.GetPosition(count));
                return;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new NanoMsgException("Connection closed before the SP handshake was written.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static async ValueTask<(bool Ok, ulong Length)> ReadLengthAsync(
        PipeReader reader,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.Length >= SpFraming.LengthPrefixSize)
            {
                Span<byte> lengthBytes = stackalloc byte[SpFraming.LengthPrefixSize];
                buffer.Slice(0, SpFraming.LengthPrefixSize).CopyTo(lengthBytes);
                ulong length = BinaryPrimitives.ReadUInt64BigEndian(lengthBytes);
                reader.AdvanceTo(buffer.GetPosition(SpFraming.LengthPrefixSize));
                return (true, length);
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                return (false, 0);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private async Task StreamBodyAsync(PipeReader reader, ulong length, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            await _socket.SendAsync(
                ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ulong remaining = length;
        while (remaining > 0)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.IsEmpty && result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new NanoMsgException("Connection closed in the middle of a message.");
            }

            long take = (long)Math.Min(remaining, (ulong)buffer.Length);
            ReadOnlySequence<byte> chunk = buffer.Slice(0, take);
            remaining -= (ulong)take;
            await SendChunkAsync(chunk, endOfMessage: remaining == 0, cancellationToken).ConfigureAwait(false);
            reader.AdvanceTo(chunk.End);
        }
    }

    private async Task SendChunkAsync(
        ReadOnlySequence<byte> chunk,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        if (chunk.IsSingleSegment)
        {
            await _socket.SendAsync(chunk.First, WebSocketMessageType.Binary, endOfMessage, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        long total = chunk.Length;
        long sent = 0;
        foreach (ReadOnlyMemory<byte> segment in chunk)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            sent += segment.Length;
            await _socket.SendAsync(
                segment, WebSocketMessageType.Binary, endOfMessage && sent == total, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
