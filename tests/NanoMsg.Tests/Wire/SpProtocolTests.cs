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
}
