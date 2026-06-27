// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers;
using System.IO.Pipelines;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// Performs the one-time SP connection handshake over an established <see cref="INanoConnection"/>:
/// each side writes its 8-byte <see cref="SpHeader"/>, reads the peer's header, and validates that
/// the two advertised protocols are compatible counterparts.
/// </summary>
internal static class SpHandshake
{
    /// <summary>Exchanges and validates SP headers, returning the peer's advertised protocol.</summary>
    /// <param name="connection">The established raw byte connection.</param>
    /// <param name="localProtocol">The protocol advertised by this endpoint.</param>
    /// <param name="cancellationToken">A token used to cancel the handshake.</param>
    /// <returns>The protocol advertised by the peer.</returns>
    /// <exception cref="NanoMsgException">The peer header was malformed, missing, or incompatible.</exception>
    public static async ValueTask<SpProtocol> PerformAsync(
        INanoConnection connection,
        SpProtocol localProtocol,
        CancellationToken cancellationToken)
    {
        WriteHeader(connection.Output, localProtocol);
        await connection.Output.FlushAsync(cancellationToken).ConfigureAwait(false);

        SpProtocol peer = await ReadHeaderAsync(connection.Input, cancellationToken).ConfigureAwait(false);
        if (!localProtocol.IsCompatibleWith(peer))
        {
            throw new NanoMsgException(
                $"Protocol mismatch: local {localProtocol} is not compatible with peer {peer}.");
        }

        return peer;
    }

    private static void WriteHeader(PipeWriter writer, SpProtocol protocol)
    {
        Span<byte> span = writer.GetSpan(SpHeader.Size);
        new SpHeader(protocol).WriteTo(span);
        writer.Advance(SpHeader.Size);
    }

    private static async ValueTask<SpProtocol> ReadHeaderAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (TryParseHeader(buffer, out SpProtocol protocol))
            {
                reader.AdvanceTo(buffer.GetPosition(SpHeader.Size));
                return protocol;
            }

            if (result.IsCompleted)
            {
                throw new NanoMsgException("Connection closed before the SP handshake completed.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool TryParseHeader(in ReadOnlySequence<byte> buffer, out SpProtocol protocol)
    {
        protocol = default;
        if (buffer.Length < SpHeader.Size)
        {
            return false;
        }

        Span<byte> headerBytes = stackalloc byte[SpHeader.Size];
        buffer.Slice(0, SpHeader.Size).CopyTo(headerBytes);
        if (!SpHeader.TryParse(headerBytes, out SpHeader header))
        {
            throw new NanoMsgException("Malformed SP connection header from peer.");
        }

        protocol = header.Protocol;
        return true;
    }
}
