using System;
using Multiplayer.Core.Jobs;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Jobs;

[TestFixture]
public sealed class JobBookletIssuanceIndexTests
{
    private static readonly Guid Host = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Client = new("00000000-0000-0000-0000-000000000002");

    [Test]
    public void TeammatesReceiveDifferentCopiesForTheSameJob()
    {
        JobBookletIssuanceIndex index = new();

        Assert.That(index.Assign(Host, 700), Is.True);
        Assert.That(index.Assign(Client, 701), Is.True);

        Assert.That(index.TryGetItem(Host, out ushort hostCopy), Is.True);
        Assert.That(index.TryGetItem(Client, out ushort clientCopy), Is.True);
        Assert.That(hostCopy, Is.EqualTo(700));
        Assert.That(clientCopy, Is.EqualTo(701));
        Assert.That(index.Count, Is.EqualTo(2));
    }

    [Test]
    public void ReissuingAPlayerReplacesOnlyThatPlayersMapping()
    {
        JobBookletIssuanceIndex index = new();
        index.Assign(Host, 700);
        index.Assign(Client, 701);

        index.Assign(Host, 702);

        Assert.That(index.TryGetItem(Host, out ushort replacement), Is.True);
        Assert.That(replacement, Is.EqualTo(702));
        Assert.That(index.TryGetPlayer(700, out _), Is.False);
        Assert.That(index.TryGetItem(Client, out ushort teammate), Is.True);
        Assert.That(teammate, Is.EqualTo(701));
    }

    [Test]
    public void OnePhysicalCopyCannotBeIssuedToTwoPlayers()
    {
        JobBookletIssuanceIndex index = new();
        index.Assign(Host, 700);

        index.Assign(Client, 700);

        Assert.That(index.TryGetItem(Host, out _), Is.False);
        Assert.That(index.TryGetItem(Client, out ushort item), Is.True);
        Assert.That(item, Is.EqualTo(700));
        Assert.That(index.TryGetPlayer(700, out Guid player), Is.True);
        Assert.That(player, Is.EqualTo(Client));
    }

    [Test]
    public void DestroyedCopyMakesItsPlayerEligibleForAReplacement()
    {
        JobBookletIssuanceIndex index = new();
        index.Assign(Client, 701);

        Assert.That(index.RemoveItem(701), Is.True);

        Assert.That(index.TryGetItem(Client, out _), Is.False);
        Assert.That(index.Count, Is.Zero);
        Assert.That(index.Assign(Client, 702), Is.True);
    }

    [Test]
    public void ZeroIdentityCannotBeIssued()
    {
        JobBookletIssuanceIndex index = new();

        Assert.That(index.Assign(Guid.Empty, 700), Is.False);
        Assert.That(index.Assign(Host, 0), Is.False);
        Assert.That(index.Count, Is.Zero);
    }
}
