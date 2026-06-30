// Copyright (c) marcschier. Licensed under the MIT License.

using NanoMsg.Transports;
using NanoMsg.Wire;

namespace NanoMsg.Tests.Transports;

public sealed class UdpSpHeaderTests
{
    [Test]
    public async Task Roundtrips_all_fields()
    {
        UdpSpHeader original = new(
            UdpSpHeader.OpData,
            SpProtocol.Push,
            param0: 5,
            param1: 7,
            senderId: 0x01020304,
            peerId: 0x05060708);

        byte[] buffer = new byte[UdpSpHeader.Size];
        original.WriteTo(buffer);

        await Assert.That(UdpSpHeader.TryParse(buffer, out UdpSpHeader parsed)).IsTrue();
        await Assert.That(parsed.OpCode).IsEqualTo(UdpSpHeader.OpData);
        await Assert.That(parsed.Protocol).IsEqualTo(SpProtocol.Push);
        await Assert.That(parsed.Param0).IsEqualTo((ushort)5);
        await Assert.That(parsed.Param1).IsEqualTo((ushort)7);
        await Assert.That(parsed.SenderId).IsEqualTo(0x01020304u);
        await Assert.That(parsed.PeerId).IsEqualTo(0x05060708u);
    }

    [Test]
    public async Task Writes_version_opcode_big_endian_fields_and_clears_reserved()
    {
        UdpSpHeader header = new(
            UdpSpHeader.OpCreq,
            SpProtocol.Sub, // SUB = 33 = 0x0021
            param0: 0x1122,
            param1: 0x3344,
            senderId: 0x01020304,
            peerId: 0x05060708);

        byte[] buffer = new byte[UdpSpHeader.Size];
        // Pre-fill so the reserved-bytes clear is observable.
        Array.Fill(buffer, (byte)0xFF);
        header.WriteTo(buffer);

        byte[] expected =
        [
            UdpSpHeader.Version, // [0] version
            UdpSpHeader.OpCreq,  // [1] op-code
            0x00, 0x21,          // [2..3] protocol (SUB) big-endian
            0x11, 0x22,          // [4..5] param0 big-endian
            0x33, 0x44,          // [6..7] param1 big-endian
            0x01, 0x02, 0x03, 0x04, // [8..11] senderId big-endian
            0x05, 0x06, 0x07, 0x08, // [12..15] peerId big-endian
            0x00, 0x00, 0x00, 0x00, // [16..19] reserved (cleared)
        ];

        await Assert.That(buffer.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task TryParse_rejects_wrong_version()
    {
        byte[] buffer = new byte[UdpSpHeader.Size];
        new UdpSpHeader(UdpSpHeader.OpData, SpProtocol.Pull, 0, 0, 1, 2).WriteTo(buffer);
        buffer[0] = UdpSpHeader.Version + 1; // corrupt the version byte

        await Assert.That(UdpSpHeader.TryParse(buffer, out _)).IsFalse();
    }

    [Test]
    public async Task TryParse_rejects_short_input()
    {
        byte[] buffer = new byte[UdpSpHeader.Size - 1];
        await Assert.That(UdpSpHeader.TryParse(buffer, out _)).IsFalse();
    }

    [Test]
    public async Task Roundtrips_every_opcode()
    {
        byte[] opCodes = [UdpSpHeader.OpCreq, UdpSpHeader.OpCack, UdpSpHeader.OpData, UdpSpHeader.OpDisc];
        foreach (byte opCode in opCodes)
        {
            byte[] buffer = new byte[UdpSpHeader.Size];
            new UdpSpHeader(opCode, SpProtocol.Bus, 1, 2, 3, 4).WriteTo(buffer);

            await Assert.That(UdpSpHeader.TryParse(buffer, out UdpSpHeader parsed)).IsTrue();
            await Assert.That(parsed.OpCode).IsEqualTo(opCode);
        }
    }
}
