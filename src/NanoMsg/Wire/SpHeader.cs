// Copyright (c) marcschier. Licensed under the MIT License.

using System.Buffers.Binary;

namespace NanoMsg.Wire;

/// <summary>
/// The fixed 8-byte SP connection header exchanged once at the start of every stream or WebSocket
/// connection: the magic bytes <c>0x00 'S' 'P' 0x00</c>, a 16-bit big-endian protocol number, then
/// two reserved zero bytes. The header is sent a single time per connection; subsequent messages are
/// length-prefixed frames (see <see cref="SpFraming"/>).
/// </summary>
internal readonly struct SpHeader
{
    /// <summary>The size of the encoded header, in bytes.</summary>
    public const int Size = 8;

    private static ReadOnlySpan<byte> Magic => [0x00, 0x53, 0x50, 0x00];

    /// <summary>Initializes a new instance of the <see cref="SpHeader"/> struct.</summary>
    /// <param name="protocol">The scalability protocol to advertise.</param>
    public SpHeader(SpProtocol protocol) => Protocol = protocol;

    /// <summary>Gets the scalability protocol advertised by the sender.</summary>
    public SpProtocol Protocol { get; }

    /// <summary>Encodes the header into the start of <paramref name="destination"/>.</summary>
    /// <param name="destination">A buffer of at least <see cref="Size"/> bytes.</param>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"Destination must be at least {Size} bytes.", nameof(destination));
        }

        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4), (ushort)Protocol);
        destination[6] = 0;
        destination[7] = 0;
    }

    /// <summary>Attempts to decode an SP header from the start of <paramref name="source"/>.</summary>
    /// <param name="source">The candidate header bytes.</param>
    /// <param name="header">When this method returns <see langword="true"/>, the decoded header.</param>
    /// <returns><see langword="true"/> if the magic prefix matched and a header was decoded.</returns>
    public static bool TryParse(ReadOnlySpan<byte> source, out SpHeader header)
    {
        header = default;
        if (source.Length < Size)
        {
            return false;
        }

        if (!source.Slice(0, 4).SequenceEqual(Magic))
        {
            return false;
        }

        ushort protocol = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(4));
        header = new SpHeader((SpProtocol)protocol);
        return true;
    }
}
