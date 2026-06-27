// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.Threading.Channels;
using NanoMsg.Wire;

namespace NanoMsg.Protocols;

/// <summary>
/// SURVEYOR: broadcasts a survey (a 4-byte survey id with the high bit set, prepended to the body) to
/// every respondent, then collects matching responses until the survey deadline elapses. One survey is
/// active at a time; responses carrying a stale id are discarded.
/// </summary>
internal sealed class SurveyorCore : NanoSocketCore
{
    private int _counter;
    private volatile SurveyState? _current;

    public SurveyorCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Surveyor, options)
    {
    }

    protected override bool DeliversInbound => false;

    public async ValueTask<IReadOnlyList<NanoMessage>> SurveyAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        uint id = NextSurveyId();
        SurveyState state = new(id);
        _current = state;
        List<NanoMessage> responses = [];
        try
        {
            await BroadcastAsync(id, body, cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Options.SurveyDeadline);
            try
            {
                await foreach (NanoMessage response in
                    state.Responses.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    responses.Add(response);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The survey deadline elapsed; return whatever responses were collected.
            }

            return responses;
        }
        finally
        {
            _current = null;
            state.Responses.Writer.TryComplete();
            while (state.Responses.Reader.TryRead(out NanoMessage? leftover))
            {
                leftover.Dispose();
            }
        }
    }

    protected override void OnFrame(NanoPipe pipe, in ReadOnlySequence<byte> frame)
    {
        if (!TryReadHeader(frame, out uint id, out ReadOnlySequence<byte> payload))
        {
            return;
        }

        SurveyState? state = _current;
        if (state is null || state.Id != id)
        {
            return;
        }

        NanoMessage message = NanoMessage.CopyFrom(payload);
        if (!state.Responses.Writer.TryWrite(message))
        {
            message.Dispose();
        }
    }

    private uint NextSurveyId() => 0x8000_0000u | ((uint)Interlocked.Increment(ref _counter) & 0x7FFF_FFFFu);

    private sealed class SurveyState
    {
        public SurveyState(uint id) => Id = id;

        public uint Id { get; }

        public Channel<NanoMessage> Responses { get; } = Channel.CreateUnbounded<NanoMessage>();
    }
}

/// <summary>
/// RESPONDENT: fair-queues inbound surveys (stripping the 4-byte survey id) and responds on the same
/// pipe with the same id so the surveyor can correlate the response with the active survey.
/// </summary>
internal sealed class RespondentCore : NanoSocketCore
{
    public RespondentCore(NanoSocketOptions? options = null)
        : base(SpProtocol.Respondent, options)
    {
    }

    public async ValueTask<RespondentExchange> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        InboundMessage inbound = await ReceiveInboundAsync(cancellationToken).ConfigureAwait(false);
        return new RespondentExchange(inbound.Source, inbound.Header, inbound.Message);
    }

    protected override void OnFrame(NanoPipe pipe, in ReadOnlySequence<byte> frame)
    {
        if (!TryReadHeader(frame, out uint id, out ReadOnlySequence<byte> payload))
        {
            return;
        }

        EnqueueInbound(new InboundMessage(pipe, NanoMessage.CopyFrom(payload), id));
    }

    internal static ValueTask RespondAsync(
        NanoPipe pipe,
        uint surveyId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct) =>
        TrySendToPipeAsync(pipe, surveyId, body, ct);
}

/// <summary>A single received survey together with the means to respond to its surveyor.</summary>
internal sealed class RespondentExchange : IDisposable
{
    private readonly NanoPipe _pipe;
    private readonly uint _surveyId;

    public RespondentExchange(NanoPipe pipe, uint surveyId, NanoMessage survey)
    {
        _pipe = pipe;
        _surveyId = surveyId;
        Survey = survey;
    }

    /// <summary>Gets the survey payload (the 4-byte id has already been stripped).</summary>
    public NanoMessage Survey { get; }

    /// <summary>Sends <paramref name="body"/> back to the surveyor.</summary>
    public ValueTask RespondAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) =>
        RespondentCore.RespondAsync(_pipe, _surveyId, body, cancellationToken);

    /// <summary>Returns the survey payload to the pool.</summary>
    public void Dispose() => Survey.Dispose();
}
