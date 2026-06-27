// Copyright (c) marcschier. Licensed under the MIT License.

using NanoMsg.Wire;

namespace NanoMsg.Tests.Wire;

public sealed class SpHeaderTests
{
    private static readonly SpProtocol[] AllProtocols =
    [
        SpProtocol.Pair, SpProtocol.Pub, SpProtocol.Sub, SpProtocol.Req, SpProtocol.Rep,
        SpProtocol.Push, SpProtocol.Pull, SpProtocol.Surveyor, SpProtocol.Respondent, SpProtocol.Bus,
    ];

    [Test]
    public async Task Roundtrips_every_protocol()
    {
        foreach (SpProtocol protocol in AllProtocols)
        {
            byte[] buffer = new byte[SpHeader.Size];
            new SpHeader(protocol).WriteTo(buffer);

            await Assert.That(SpHeader.TryParse(buffer, out SpHeader parsed)).IsTrue();
            await Assert.That(parsed.Protocol).IsEqualTo(protocol);
        }
    }

    [Test]
    public async Task Writes_canonical_magic_and_big_endian_protocol()
    {
        byte[] buffer = new byte[SpHeader.Size];
        new SpHeader(SpProtocol.Sub).WriteTo(buffer); // SUB = 33 = 0x0021

        byte[] expected = [0x00, 0x53, 0x50, 0x00, 0x00, 0x21, 0x00, 0x00];
        await Assert.That(buffer.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task TryParse_rejects_bad_magic()
    {
        byte[] buffer = [0x00, 0x53, 0x51, 0x00, 0x00, 0x20, 0x00, 0x00];
        await Assert.That(SpHeader.TryParse(buffer, out _)).IsFalse();
    }

    [Test]
    public async Task TryParse_rejects_short_input()
    {
        byte[] buffer = [0x00, 0x53, 0x50];
        await Assert.That(SpHeader.TryParse(buffer, out _)).IsFalse();
    }

    [Test]
    public async Task WriteTo_throws_on_small_destination()
    {
        bool threw = false;
        try
        {
            new SpHeader(SpProtocol.Pair).WriteTo(new byte[4]);
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
