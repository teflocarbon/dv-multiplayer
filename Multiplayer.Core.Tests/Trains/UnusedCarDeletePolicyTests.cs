using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class UnusedCarDeletePolicyTests
{
    [Test]
    public void Deletes_WhenEveryPlayerIsPlaced()
    {
        Assert.That(UnusedCarDeletePolicy.MayDelete(players: 1, placed: 1), Is.True);
        Assert.That(UnusedCarDeletePolicy.MayDelete(players: 3, placed: 3), Is.True);
    }

    [Test]
    public void Keeps_WithNobodyInTheGame()
    {
        // A dedicated host with nobody online: its parked body isn't a player.
        Assert.That(UnusedCarDeletePolicy.MayDelete(players: 0, placed: 0), Is.False);
    }

    [Test]
    public void Keeps_WhileTheOnlyPlayerIsStillLoading()
    {
        // The 2026-10-19 crash restart: one player logging in, no position yet.
        Assert.That(UnusedCarDeletePolicy.MayDelete(players: 1, placed: 0), Is.False);
    }

    [Test]
    public void Keeps_WhileAnyPlayerIsStillLoading()
    {
        // A loading player may be next to a train the placed players are far from.
        Assert.That(UnusedCarDeletePolicy.MayDelete(players: 3, placed: 2), Is.False);
    }
}
