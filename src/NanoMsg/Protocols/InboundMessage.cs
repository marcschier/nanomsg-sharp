// Copyright (c) marcschier. Licensed under the MIT License.

namespace NanoMsg.Protocols;

/// <summary>An inbound message paired with the pipe it arrived on (needed for reply routing).</summary>
internal readonly struct InboundMessage
{
    /// <summary>Initializes a new instance of the <see cref="InboundMessage"/> struct.</summary>
    /// <param name="source">The pipe the message arrived on.</param>
    /// <param name="message">The owned payload.</param>
    /// <param name="header">The 4-byte request/survey id, or 0 when the protocol has no header.</param>
    public InboundMessage(NanoPipe source, NanoMessage message, uint header = 0)
    {
        Source = source;
        Message = message;
        Header = header;
    }

    /// <summary>Gets the pipe the message arrived on.</summary>
    public NanoPipe Source { get; }

    /// <summary>Gets the owned payload.</summary>
    public NanoMessage Message { get; }

    /// <summary>Gets the 4-byte request/survey id, or 0 when the protocol has no header.</summary>
    public uint Header { get; }
}
