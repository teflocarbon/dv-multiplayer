using Multiplayer.Core.Jobs;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Jobs;

[TestFixture]
public sealed class JobBookletIssuanceIndexTests
{
    [Test]
    public void TeammatesReceiveDifferentCopiesForTheSameJob()
    {
        JobBookletIssuanceIndex index = new();

        Assert.That(index.Assign(1, 700), Is.True);
        Assert.That(index.Assign(2, 701), Is.True);

        Assert.That(index.TryGetItem(1, out ushort hostCopy), Is.True);
        Assert.That(index.TryGetItem(2, out ushort clientCopy), Is.True);
        Assert.That(hostCopy, Is.EqualTo(700));
        Assert.That(clientCopy, Is.EqualTo(701));
        Assert.That(index.Count, Is.EqualTo(2));
    }

    [Test]
    public void ReissuingAPlayerReplacesOnlyThatPlayersMapping()
    {
        JobBookletIssuanceIndex index = new();
        index.Assign(1, 700);
        index.Assign(2, 701);

        index.Assign(1, 702);

        Assert.That(index.TryGetItem(1, out ushort replacement), Is.True);
        Assert.That(replacement, Is.EqualTo(702));
        Assert.That(index.TryGetPlayer(700, out _), Is.False);
        Assert.That(index.TryGetItem(2, out ushort teammate), Is.True);
        Assert.That(teammate, Is.EqualTo(701));
    }

    [Test]
    public void OnePhysicalCopyCannotBeIssuedToTwoPlayers()
    {
        JobBookletIssuanceIndex index = new();
        index.Assign(1, 700);

        index.Assign(2, 700);

        Assert.That(index.TryGetItem(1, out _), Is.False);
        Assert.That(index.TryGetItem(2, out ushort item), Is.True);
        Assert.That(item, Is.EqualTo(700));
        Assert.That(index.TryGetPlayer(700, out byte player), Is.True);
        Assert.That(player, Is.EqualTo(2));
    }

    [Test]
    public void DestroyedCopyMakesItsPlayerEligibleForAReplacement()
    {
        JobBookletIssuanceIndex index = new();
        index.Assign(2, 701);

        Assert.That(index.RemoveItem(701), Is.True);

        Assert.That(index.TryGetItem(2, out _), Is.False);
        Assert.That(index.Count, Is.Zero);
        Assert.That(index.Assign(2, 702), Is.True);
    }

    [TestCase(0, 700)]
    [TestCase(1, 0)]
    public void ZeroIdentityCannotBeIssued(byte playerId, int itemNetId)
    {
        JobBookletIssuanceIndex index = new();

        Assert.That(index.Assign(playerId, (ushort)itemNetId), Is.False);
        Assert.That(index.Count, Is.Zero);
    }
}
