// Copyright (c) marcschier. Licensed under the MIT License.

using System.Text;
using NanoMsg.Protocols;

namespace NanoMsg;

/// <summary>PAIR socket: a single bidirectional connection to one peer.</summary>
public sealed class PairSocket : NanoSocket
{
    private readonly PairCore _core;

    /// <summary>Initializes a new <see cref="PairSocket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public PairSocket(NanoSocketOptions? options = null)
        : this(new PairCore(options))
    {
    }

    private PairSocket(PairCore core)
        : base(core) => _core = core;

    /// <summary>Sends <paramref name="body"/> to the peer.</summary>
    /// <param name="body">The message payload.</param>
    /// <param name="cancellationToken">A token used to cancel the send.</param>
    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        _core.SendAsync(body, cancellationToken);

    /// <summary>Receives the next message; dispose the returned message when done.</summary>
    /// <param name="cancellationToken">A token used to cancel the receive.</param>
    /// <returns>The received message.</returns>
    public ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _core.ReceiveAsync(cancellationToken);
}

/// <summary>PUB socket: broadcasts every message to all connected subscribers.</summary>
public sealed class PublishSocket : NanoSocket
{
    private readonly PubCore _core;

    /// <summary>Initializes a new <see cref="PublishSocket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public PublishSocket(NanoSocketOptions? options = null)
        : this(new PubCore(options))
    {
    }

    private PublishSocket(PubCore core)
        : base(core) => _core = core;

    /// <summary>Publishes <paramref name="body"/> to every subscriber.</summary>
    /// <param name="body">The message payload (subscribers filter by its leading bytes).</param>
    /// <param name="cancellationToken">A token used to cancel the send.</param>
    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        _core.SendAsync(body, cancellationToken);
}

/// <summary>SUB socket: receives published messages matching one of its subscription prefixes.</summary>
public sealed class SubscribeSocket : NanoSocket
{
    private readonly SubCore _core;

    /// <summary>Initializes a new <see cref="SubscribeSocket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public SubscribeSocket(NanoSocketOptions? options = null)
        : this(new SubCore(options))
    {
    }

    private SubscribeSocket(SubCore core)
        : base(core) => _core = core;

    /// <summary>
    /// Subscribes to messages whose payload starts with <paramref name="prefix"/> (empty matches all).
    /// </summary>
    /// <param name="prefix">The subscription prefix.</param>
    public void Subscribe(ReadOnlySpan<byte> prefix) => _core.Subscribe(prefix);

    /// <summary>
    /// Subscribes to messages whose payload starts with the UTF-8 bytes of <paramref name="prefix"/>.
    /// </summary>
    /// <param name="prefix">The subscription prefix.</param>
    public void Subscribe(string prefix) => _core.Subscribe(Encoding.UTF8.GetBytes(prefix));

    /// <summary>Removes a previously registered byte subscription.</summary>
    /// <param name="prefix">The subscription prefix to remove.</param>
    public void Unsubscribe(ReadOnlySpan<byte> prefix) => _core.Unsubscribe(prefix);

    /// <summary>Removes a previously registered string subscription.</summary>
    /// <param name="prefix">The subscription prefix to remove.</param>
    public void Unsubscribe(string prefix) => _core.Unsubscribe(Encoding.UTF8.GetBytes(prefix));

    /// <summary>Receives the next matching message; dispose the returned message when done.</summary>
    /// <param name="cancellationToken">A token used to cancel the receive.</param>
    /// <returns>The received message.</returns>
    public ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _core.ReceiveAsync(cancellationToken);
}

/// <summary>PUSH socket: load-balances each message to one connected puller.</summary>
public sealed class PushSocket : NanoSocket
{
    private readonly PushCore _core;

    /// <summary>Initializes a new <see cref="PushSocket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public PushSocket(NanoSocketOptions? options = null)
        : this(new PushCore(options))
    {
    }

    private PushSocket(PushCore core)
        : base(core) => _core = core;

    /// <summary>Sends <paramref name="body"/> to the next puller in round-robin order.</summary>
    /// <param name="body">The message payload.</param>
    /// <param name="cancellationToken">A token used to cancel the send.</param>
    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        _core.SendAsync(body, cancellationToken);
}

/// <summary>PULL socket: fair-queues messages from all connected pushers.</summary>
public sealed class PullSocket : NanoSocket
{
    private readonly PullCore _core;

    /// <summary>Initializes a new <see cref="PullSocket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public PullSocket(NanoSocketOptions? options = null)
        : this(new PullCore(options))
    {
    }

    private PullSocket(PullCore core)
        : base(core) => _core = core;

    /// <summary>Receives the next message; dispose the returned message when done.</summary>
    /// <param name="cancellationToken">A token used to cancel the receive.</param>
    /// <returns>The received message.</returns>
    public ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _core.ReceiveAsync(cancellationToken);
}

/// <summary>REQ socket: sends a request and awaits the correlated reply, resending on timeout.</summary>
public sealed class RequestSocket : NanoSocket
{
    private readonly ReqCore _core;

    /// <summary>Initializes a new <see cref="RequestSocket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public RequestSocket(NanoSocketOptions? options = null)
        : this(new ReqCore(options))
    {
    }

    private RequestSocket(ReqCore core)
        : base(core) => _core = core;

    /// <summary>Sends a request and awaits the reply; dispose the returned message when done.</summary>
    /// <param name="body">The request payload.</param>
    /// <param name="cancellationToken">A token used to cancel the request.</param>
    /// <returns>The reply message.</returns>
    public ValueTask<NanoMessage> RequestAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default) =>
        _core.RequestAsync(body, cancellationToken);
}

/// <summary>REP socket: receives a request, then replies to it (strict receive-then-reply order).</summary>
public sealed class ReplySocket : NanoSocket
{
    private readonly RepCore _core;
    private RepExchange? _pending;

    /// <summary>Initializes a new <see cref="ReplySocket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public ReplySocket(NanoSocketOptions? options = null)
        : this(new RepCore(options))
    {
    }

    private ReplySocket(RepCore core)
        : base(core) => _core = core;

    /// <summary>
    /// Receives the next request; dispose the returned message when done, then call <see cref="ReplyAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the receive.</param>
    /// <returns>The request message.</returns>
    public async ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        _pending = await _core.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return _pending.Request;
    }

    /// <summary>Replies to the most recently received request.</summary>
    /// <param name="body">The reply payload.</param>
    /// <param name="cancellationToken">A token used to cancel the reply.</param>
    /// <exception cref="NanoMsgException">No request has been received since the last reply.</exception>
    public ValueTask ReplyAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        RepExchange pending = _pending
            ?? throw new NanoMsgException("ReplyAsync was called before a request was received.");
        _pending = null;
        return pending.ReplyAsync(body, cancellationToken);
    }
}

/// <summary>SURVEYOR socket: broadcasts a survey and collects responses until the deadline elapses.</summary>
public sealed class SurveyorSocket : NanoSocket
{
    private readonly SurveyorCore _core;

    /// <summary>Initializes a new <see cref="SurveyorSocket"/>.</summary>
    /// <param name="options">Optional socket tuning (including <see cref="NanoSocketOptions.SurveyDeadline"/>).</param>
    public SurveyorSocket(NanoSocketOptions? options = null)
        : this(new SurveyorCore(options))
    {
    }

    private SurveyorSocket(SurveyorCore core)
        : base(core) => _core = core;

    /// <summary>Broadcasts a survey and returns the responses received before the deadline; dispose each.</summary>
    /// <param name="body">The survey payload.</param>
    /// <param name="cancellationToken">A token used to cancel the survey.</param>
    /// <returns>The collected responses.</returns>
    public ValueTask<IReadOnlyList<NanoMessage>> SurveyAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default) =>
        _core.SurveyAsync(body, cancellationToken);
}

/// <summary>RESPONDENT socket: receives a survey, then responds to it.</summary>
public sealed class RespondentSocket : NanoSocket
{
    private readonly RespondentCore _core;
    private RespondentExchange? _pending;

    /// <summary>Initializes a new <see cref="RespondentSocket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public RespondentSocket(NanoSocketOptions? options = null)
        : this(new RespondentCore(options))
    {
    }

    private RespondentSocket(RespondentCore core)
        : base(core) => _core = core;

    /// <summary>
    /// Receives the next survey; dispose the returned message when done, then call <see cref="RespondAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the receive.</param>
    /// <returns>The survey message.</returns>
    public async ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        _pending = await _core.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return _pending.Survey;
    }

    /// <summary>Responds to the most recently received survey.</summary>
    /// <param name="body">The response payload.</param>
    /// <param name="cancellationToken">A token used to cancel the response.</param>
    /// <exception cref="NanoMsgException">No survey has been received since the last response.</exception>
    public ValueTask RespondAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        RespondentExchange pending = _pending
            ?? throw new NanoMsgException("RespondAsync was called before a survey was received.");
        _pending = null;
        return pending.RespondAsync(body, cancellationToken);
    }
}

/// <summary>BUS socket: broadcasts each message to all peers and fair-queues messages from all peers.</summary>
public sealed class BusSocket : NanoSocket
{
    private readonly BusCore _core;

    /// <summary>Initializes a new <see cref="BusSocket"/>.</summary>
    /// <param name="options">Optional socket tuning.</param>
    public BusSocket(NanoSocketOptions? options = null)
        : this(new BusCore(options))
    {
    }

    private BusSocket(BusCore core)
        : base(core) => _core = core;

    /// <summary>Sends <paramref name="body"/> to every connected peer.</summary>
    /// <param name="body">The message payload.</param>
    /// <param name="cancellationToken">A token used to cancel the send.</param>
    public ValueTask SendAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        _core.SendAsync(body, cancellationToken);

    /// <summary>Receives the next message; dispose the returned message when done.</summary>
    /// <param name="cancellationToken">A token used to cancel the receive.</param>
    /// <returns>The received message.</returns>
    public ValueTask<NanoMessage> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _core.ReceiveAsync(cancellationToken);
}
