// Copyright (c) marcschier. Licensed under the MIT License.

using NanoMsg.Protocols;

namespace NanoMsg.Tests.Protocols;

public sealed class NanoSocketCoreTests
{
    [Test]
    public async Task NextReconnectDelay_doubles_while_below_the_ceiling()
    {
        TimeSpan next = NanoSocketCore.NextReconnectDelay(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));

        await Assert.That(next).IsEqualTo(TimeSpan.FromMilliseconds(200));
    }

    [Test]
    public async Task NextReconnectDelay_clamps_to_the_ceiling()
    {
        // 3s doubled (6s) exceeds the 5s ceiling, so the result is clamped, not the larger value.
        TimeSpan clamped = NanoSocketCore.NextReconnectDelay(
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        await Assert.That(clamped).IsEqualTo(TimeSpan.FromSeconds(5));

        // Once at the ceiling it stays there rather than growing without bound.
        TimeSpan stable = NanoSocketCore.NextReconnectDelay(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await Assert.That(stable).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task NextReconnectDelay_does_not_grow_when_max_is_non_positive()
    {
        TimeSpan current = TimeSpan.FromMilliseconds(100);

        await Assert.That(NanoSocketCore.NextReconnectDelay(current, TimeSpan.Zero)).IsEqualTo(current);
        await Assert.That(NanoSocketCore.NextReconnectDelay(current, TimeSpan.FromSeconds(-1)))
            .IsEqualTo(current);
    }

    [Test]
    public async Task EffectiveMaxBody_treats_negative_as_unlimited()
    {
        await Assert.That(NanoSocketCore.EffectiveMaxBody(-1)).IsEqualTo(long.MaxValue);
        await Assert.That(NanoSocketCore.EffectiveMaxBody(0)).IsEqualTo(0L);
        await Assert.That(NanoSocketCore.EffectiveMaxBody(1024 * 1024)).IsEqualTo(1024L * 1024L);
    }
}
