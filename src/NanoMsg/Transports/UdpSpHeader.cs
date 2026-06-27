// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers.Binary;
using NanoMsg.Wire;

namespace NanoMsg.Transports;

/// <summary>
/// The fixed 20-byte UDP transport header, modelled on NNG's experimental <c>udp_sp_msg</c>: a version,
/// an op-code (connection request/ack, data, or disconnect), the sender's SP protocol number, two
/// 16-bit parameters (data length / advertised receive-max / refresh / disconnect reason), and the two
/// per-peer connection ids. NanoMsgSharp's framing is modelled on NNG's but is <em>not</em> wire-verified
/// against native NNG (the experimental transport is absent from released <c>libnng</c> packages).
/// </summary>
internal readonly struct UdpSpHeader
{
    /// <summary>The encoded header size, in bytes.</summary>
    public const int Size = 20;

    /// <summary>The current header version.</summary>
    public const byte Version = 1;

    /// <summary>Op-code: a data message whose payload is one SP message.</summary>
    public const byte OpData = 0;

    /// <summary>Op-code: connection request (handshake, from the dialer).</summary>
    public const byte OpCreq = 1;

    /// <summary>Op-code: connection acknowledgement (handshake, from the listener).</summary>
    public const byte OpCack = 2;

    /// <summary>Op-code: disconnect.</summary>
    public const byte OpDisc = 3;

    /// <summary>Initializes a new instance of the <see cref="UdpSpHeader"/> struct.</summary>
    /// <param name="opCode">The op-code.</param>
    /// <param name="protocol">The sender's SP protocol number.</param>
    /// <param name="param0">Data length / receive-max / reason.</param>
    /// <param name="param1">Refresh interval (seconds) for CREQ/CACK.</param>
    /// <param name="senderId">The sender's connection id.</param>
    /// <param name="peerId">The peer's connection id (0 if unknown).</param>
    public UdpSpHeader(byte opCode, SpProtocol protocol, ushort param0, ushort param1, uint senderId, uint peerId)
    {
        OpCode = opCode;
        Protocol = protocol;
        Param0 = param0;
        Param1 = param1;
        SenderId = senderId;
        PeerId = peerId;
    }

    /// <summary>Gets the op-code.</summary>
    public byte OpCode { get; }

    /// <summary>Gets the sender's SP protocol number.</summary>
    public SpProtocol Protocol { get; }

    /// <summary>Gets the first 16-bit parameter (data length / receive-max / reason).</summary>
    public ushort Param0 { get; }

    /// <summary>Gets the second 16-bit parameter (refresh interval).</summary>
    public ushort Param1 { get; }

    /// <summary>Gets the sender's connection id.</summary>
    public uint SenderId { get; }

    /// <summary>Gets the peer's connection id.</summary>
    public uint PeerId { get; }

    /// <summary>Encodes the header into the start of <paramref name="destination"/>.</summary>
    /// <param name="destination">A buffer of at least <see cref="Size"/> bytes.</param>
    public void WriteTo(Span<byte> destination)
    {
        destination[0] = Version;
        destination[1] = OpCode;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2), (ushort)Protocol);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4), Param0);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(6), Param1);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8), SenderId);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(12), PeerId);
        destination.Slice(16, 4).Clear();
    }

    /// <summary>Attempts to decode a header from the start of <paramref name="source"/>.</summary>
    /// <param name="source">The candidate header bytes.</param>
    /// <param name="header">When this method returns <see langword="true"/>, the decoded header.</param>
    /// <returns><see langword="true"/> if a version-1 header was decoded.</returns>
    public static bool TryParse(ReadOnlySpan<byte> source, out UdpSpHeader header)
    {
        header = default;
        if (source.Length < Size || source[0] != Version)
        {
            return false;
        }

        header = new UdpSpHeader(
            source[1],
            (SpProtocol)BinaryPrimitives.ReadUInt16BigEndian(source.Slice(2)),
            BinaryPrimitives.ReadUInt16BigEndian(source.Slice(4)),
            BinaryPrimitives.ReadUInt16BigEndian(source.Slice(6)),
            BinaryPrimitives.ReadUInt32BigEndian(source.Slice(8)),
            BinaryPrimitives.ReadUInt32BigEndian(source.Slice(12)));
        return true;
    }
}
