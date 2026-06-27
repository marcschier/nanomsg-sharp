// Copyright (c) marcschier. Licensed under the MIT License.

namespace NanoMsg;

/// <summary>
/// The exception thrown when a nanomsg scalability-protocol operation fails — for example a
/// malformed wire frame, an unsupported endpoint address, or an invalid socket-state transition.
/// </summary>
public class NanoMsgException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NanoMsgException"/> class.</summary>
    public NanoMsgException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NanoMsgException"/> class.</summary>
    /// <param name="message">A message that describes the error.</param>
    public NanoMsgException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NanoMsgException"/> class.</summary>
    /// <param name="message">A message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public NanoMsgException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
