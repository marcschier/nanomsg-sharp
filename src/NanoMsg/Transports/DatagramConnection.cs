// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.Sockets;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// Adapts a message-oriented <see cref="IDatagramChannel"/> (raw UDP or DTLS) to the byte-channel
/// <see cref="INanoConnection"/> contract, exactly as <c>WebSocketConnection</c> does for
/// WebSockets: the SP protocol is negotiated out of band (by the transport handshake, not the 8-byte SP
/// header), and each SP message maps directly to one datagram with no length prefix. This connection
/// <em>synthesises</em> the SP handshake the protocol layer still performs — swallowing the locally
/// written 8-byte header and injecting the peer's (built from the negotiated peer protocol) — and
/// re-frames between the core's length-prefixed frames and whole datagram payloads.
/// </summary>
internal sealed class DatagramConnection : INanoConnection
{
    /// <summary>The largest SP message a datagram payload may carry (NNG udp <c>NNG_UDP_RECVMAX</c>).</summary>
    public const int MaxDatagramPayload = 65000;

    private readonly IDatagramChannel _channel;
    private readonly SpProtocol _peerProtocol;

    // The outbound pipe holds at most one bounded (<= 65000 + prefix) frame, so disable backpressure
    // pausing and read whole frames with SpFraming.TryReadFrame.
    private readonly Pipe _outbound = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
    private readonly Pipe _inbound = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _sendPump;
    private readonly Task _receivePump;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="DatagramConnection"/> class.</summary>
    /// <param name="channel">The owned datagram channel to the peer.</param>
    /// <param name="peerProtocol">The peer protocol learned from the transport handshake.</param>
    public DatagramConnection(IDatagramChannel channel, SpProtocol peerProtocol)
    {
        _channel = channel;
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
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException
            or InvalidOperationException or NanoMsgException or SocketException)
        {
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task SendPumpAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = _outbound.Reader;
        try
        {
            await DiscardAsync(reader, SpHeader.Size, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (SpFraming.TryReadFrame(ref buffer, out ReadOnlySequence<byte> body, MaxDatagramPayload))
                {
                    await SendMessageAsync(body, cancellationToken).ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }

            await reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException
            or InvalidOperationException or NanoMsgException or SocketException)
        {
            await reader.CompleteAsync(ex).ConfigureAwait(false);
        }
    }

    private async Task ReceivePumpAsync(CancellationToken cancellationToken)
    {
        PipeWriter writer = _inbound.Writer;
        try
        {
            Span<byte> header = writer.GetSpan(SpHeader.Size);
            new SpHeader(_peerProtocol).WriteTo(header);
            writer.Advance(SpHeader.Size);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                ReadOnlyMemory<byte>? message =
                    await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (message is not { } payload)
                {
                    break;
                }

                Span<byte> prefix = writer.GetSpan(SpFraming.LengthPrefixSize);
                BinaryPrimitives.WriteUInt64BigEndian(prefix, (ulong)payload.Length);
                writer.Advance(SpFraming.LengthPrefixSize);
                writer.Write(payload.Span);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException
            or InvalidOperationException or NanoMsgException or SocketException)
        {
            await writer.CompleteAsync(ex).ConfigureAwait(false);
        }
    }

    private async ValueTask SendMessageAsync(ReadOnlySequence<byte> body, CancellationToken cancellationToken)
    {
        if (body.IsSingleSegment)
        {
            await _channel.SendAsync(body.First, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)body.Length);
        try
        {
            body.CopyTo(buffer);
            await _channel.SendAsync(buffer.AsMemory(0, (int)body.Length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
}
