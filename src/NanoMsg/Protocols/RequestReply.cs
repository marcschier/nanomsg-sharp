// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.Collections.Concurrent;
using NanoMsg.Wire;

namespace NanoMsg.Protocols;

/// <summary>
/// REQ: load-balances each request (a 4-byte request id with the high bit set, prepended to the body)
/// across connected reply peers, then awaits the matching reply, resending on the configured interval
/// if no reply arrives. Multiple requests may be in flight concurrently; replies are correlated by id.
/// </summary>
internal sealed class ReqCore : NanoSocketCore
{
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<NanoMessage>> _pending = new();
    private int _counter;

    public ReqCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Req, options)
    {
    }

    protected override bool DeliversInbound => false;

    public async ValueTask<NanoMessage> RequestAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        uint id = NextRequestId();
        TaskCompletionSource<NanoMessage> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            TimeSpan resend = Options.RequestResendInterval;
            while (true)
            {
                await SendRoundRobinAsync(id, body, cancellationToken).ConfigureAwait(false);

                using CancellationTokenSource delayCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task timeout = Task.Delay(resend, delayCts.Token);
                Task winner = await Task.WhenAny(reply.Task, timeout).ConfigureAwait(false);
                if (winner == reply.Task)
                {
                    delayCts.Cancel();
                    return await reply.Task.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    protected override void OnFrame(NanoPipe pipe, in ReadOnlySequence<byte> frame)
    {
        if (!TryReadHeader(frame, out uint id, out ReadOnlySequence<byte> payload))
        {
            return;
        }

        if (_pending.TryGetValue(id, out TaskCompletionSource<NanoMessage>? reply))
        {
            NanoMessage message = NanoMessage.CopyFrom(payload);
            if (!reply.TrySetResult(message))
            {
                message.Dispose();
            }
        }
    }

    private uint NextRequestId() => 0x8000_0000u | ((uint)Interlocked.Increment(ref _counter) & 0x7FFF_FFFFu);
}

/// <summary>
/// REP: fair-queues inbound requests (stripping the 4-byte request id) and replies on the same pipe
/// with the same id so the request socket can correlate the answer.
/// </summary>
internal sealed class RepCore : NanoSocketCore
{
    public RepCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Rep, options)
    {
    }

    public async ValueTask<RepExchange> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        InboundMessage inbound = await ReceiveInboundAsync(cancellationToken).ConfigureAwait(false);
        return new RepExchange(inbound.Source, inbound.Header, inbound.Message);
    }

    protected override void OnFrame(NanoPipe pipe, in ReadOnlySequence<byte> frame)
    {
        if (!TryReadHeader(frame, out uint id, out ReadOnlySequence<byte> payload))
        {
            return;
        }

        EnqueueInbound(new InboundMessage(pipe, NanoMessage.CopyFrom(payload), id));
    }

    internal static ValueTask ReplyAsync(
        NanoPipe pipe,
        uint requestId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct) =>
        TrySendToPipeAsync(pipe, requestId, body, ct);
}

/// <summary>A single received request together with the means to reply to exactly its sender.</summary>
internal sealed class RepExchange : IDisposable
{
    private readonly NanoPipe _pipe;
    private readonly uint _requestId;

    public RepExchange(NanoPipe pipe, uint requestId, NanoMessage request)
    {
        _pipe = pipe;
        _requestId = requestId;
        Request = request;
    }

    /// <summary>Gets the request payload (the 4-byte id has already been stripped).</summary>
    public NanoMessage Request { get; }

    /// <summary>Sends <paramref name="body"/> back to the requester.</summary>
    public ValueTask ReplyAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        RepCore.ReplyAsync(_pipe, _requestId, body, cancellationToken);

    /// <summary>Returns the request payload to the pool.</summary>
    public void Dispose() => Request.Dispose();
}
