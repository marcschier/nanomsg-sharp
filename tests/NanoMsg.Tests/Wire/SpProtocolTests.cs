// Copyright (c) marcschier. Licensed under the MIT License.

using NanoMsg.Wire;

namespace NanoMsg.Tests.Wire;

public sealed class SpProtocolTests
{
    [Test]
    public async Task Compatibility_matrix_matches_nanomsg_counterparts()
    {
        (SpProtocol Self, SpProtocol Peer)[] compatible =
        [
            (SpProtocol.Pair, SpProtocol.Pair),
            (SpProtocol.Pub, SpProtocol.Sub),
            (SpProtocol.Sub, SpProtocol.Pub),
            (SpProtocol.Req, SpProtocol.Rep),
            (SpProtocol.Rep, SpProtocol.Req),
            (SpProtocol.Push, SpProtocol.Pull),
            (SpProtocol.Pull, SpProtocol.Push),
            (SpProtocol.Surveyor, SpProtocol.Respondent),
            (SpProtocol.Respondent, SpProtocol.Surveyor),
            (SpProtocol.Bus, SpProtocol.Bus),
        ];

        foreach ((SpProtocol self, SpProtocol peer) in compatible)
        {
            await Assert.That(self.IsCompatibleWith(peer)).IsTrue();
        }
    }

    [Test]
    public async Task Incompatible_pairs_are_rejected()
    {
        await Assert.That(SpProtocol.Pub.IsCompatibleWith(SpProtocol.Pub)).IsFalse();
        await Assert.That(SpProtocol.Req.IsCompatibleWith(SpProtocol.Sub)).IsFalse();
        await Assert.That(SpProtocol.Pair.IsCompatibleWith(SpProtocol.Bus)).IsFalse();
        await Assert.That(SpProtocol.Push.IsCompatibleWith(SpProtocol.Push)).IsFalse();
    }

    [Test]
    public async Task Family_groups_roles_of_the_same_pattern()
    {
        await Assert.That(SpProtocol.Pub.Family()).IsEqualTo(SpProtocol.Sub.Family());
        await Assert.That(SpProtocol.Req.Family()).IsEqualTo(SpProtocol.Rep.Family());
        await Assert.That(SpProtocol.Pub.Family()).IsNotEqualTo(SpProtocol.Req.Family());
    }

    [Test]
    public async Task IsDefined_distinguishes_known_protocols()
    {
        await Assert.That(SpProtocols.IsDefined(SpProtocol.Surveyor)).IsTrue();
        await Assert.That(SpProtocols.IsDefined((SpProtocol)1234)).IsFalse();
    }

    [Test]
    public async Task Pair1_is_compatible_only_with_pair1()
    {
        await Assert.That(SpProtocol.Pair1.IsCompatibleWith(SpProtocol.Pair1)).IsTrue();
        await Assert.That(SpProtocol.Pair1.IsCompatibleWith(SpProtocol.Pair)).IsFalse();
        await Assert.That(SpProtocol.Pair.IsCompatibleWith(SpProtocol.Pair1)).IsFalse();
    }

    [Test]
    public async Task Counterpart_returns_the_connecting_peer_protocol()
    {
        (SpProtocol Self, SpProtocol Counterpart)[] expected =
        [
            (SpProtocol.Pair, SpProtocol.Pair),
            (SpProtocol.Pair1, SpProtocol.Pair1),
            (SpProtocol.Pub, SpProtocol.Sub),
            (SpProtocol.Sub, SpProtocol.Pub),
            (SpProtocol.Req, SpProtocol.Rep),
            (SpProtocol.Rep, SpProtocol.Req),
            (SpProtocol.Push, SpProtocol.Pull),
            (SpProtocol.Pull, SpProtocol.Push),
            (SpProtocol.Surveyor, SpProtocol.Respondent),
            (SpProtocol.Respondent, SpProtocol.Surveyor),
            (SpProtocol.Bus, SpProtocol.Bus),
        ];

        foreach ((SpProtocol self, SpProtocol counterpart) in expected)
        {
            await Assert.That(self.Counterpart()).IsEqualTo(counterpart);
        }
    }

    [Test]
    public async Task Counterpart_throws_for_undefined_protocol()
    {
        bool threw = false;
        try
        {
            _ = ((SpProtocol)1234).Counterpart();
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task WireName_matches_the_nanomsg_protocol_names()
    {
        (SpProtocol Protocol, string Name)[] expected =
        [
            (SpProtocol.Pair, "pair"),
            (SpProtocol.Pair1, "pair1"),
            (SpProtocol.Pub, "pub"),
            (SpProtocol.Sub, "sub"),
            (SpProtocol.Req, "req"),
            (SpProtocol.Rep, "rep"),
            (SpProtocol.Push, "push"),
            (SpProtocol.Pull, "pull"),
            (SpProtocol.Surveyor, "surveyor"),
            (SpProtocol.Respondent, "respondent"),
            (SpProtocol.Bus, "bus"),
        ];

        foreach ((SpProtocol protocol, string name) in expected)
        {
            await Assert.That(protocol.WireName()).IsEqualTo(name);
        }
    }

    [Test]
    public async Task WireName_throws_for_undefined_protocol()
    {
        bool threw = false;
        try
        {
            _ = ((SpProtocol)1234).WireName();
        }
        catch (NanoMsgException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
