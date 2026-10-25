using System.Linq;
using Multiplayer.Core.Items.Physics;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Physics;

[TestFixture]
public sealed class ItemSimulatorChoiceTests
{
    private const float Range = 192f;
    private static SimulatorCandidate Server(bool solid = true) => new(0, solid, 0f, hostWorld: true, serverItself: true);
    private static SimulatorCandidate Player(byte id, float metres, bool hasItem = true, bool inCar = false, bool host = false) =>
        new(id, hasItem, metres * metres, inCar, host);

    [Test]
    public void ADedicatedHostTakesALeaseItsThrowerLeft()
    {
        // F32: the thrower (3) left, nobody else near: the server itself takes it.
        Assert.That(ItemSimulatorChoice.ForTransfer(new[] { Server(), Player(4, 900f) }, 3, Range, out byte chosen), Is.True);
        Assert.That(chosen, Is.EqualTo(0));
    }

    [Test]
    public void NotWhereItsWorldIsntSolid()
    {
        Assert.That(ItemSimulatorChoice.ForTransfer(new[] { Server(solid: false), Player(4, 900f) }, 3, Range, out _), Is.False);
    }

    [Test]
    public void ThenTheNearestPlayerInRangeWithTheItem()
    {
        Assert.That(ItemSimulatorChoice.ForTransfer(new[] { Server(solid: false), Player(4, 50f), Player(5, 20f, hasItem: false), Player(6, 80f) },
            3, Range, out byte chosen), Is.True);
        Assert.That(chosen, Is.EqualTo(4));
    }

    [Test]
    public void APlayerHostsOwnPlayerComesFirst()
    {
        Assert.That(ItemSimulatorChoice.ForTransfer(new[] { Player(2, 10f), Player(1, 150f, host: true) }, 3, Range, out byte chosen), Is.True);
        Assert.That(chosen, Is.EqualTo(1));
    }

    [Test]
    public void NeverBackToTheSimulatorThatGaveItUp()
    {
        Assert.That(ItemSimulatorChoice.ForTransfer(new[] { Player(3, 1f) }, 3, Range, out _), Is.False);
    }

    [Test]
    public void AWakeGoesToWhoeverWokeIt()
    {
        Assert.That(ItemSimulatorChoice.ForWake(new[] { Server(), Player(2, 5f, inCar: true), Player(3, 40f) }, 3, Range, out byte chosen), Is.True);
        Assert.That(chosen, Is.EqualTo(3));
    }

    [Test]
    public void ThenARiderThenANearPlayerThenTheServer()
    {
        SimulatorCandidate[] all = { Server(), Player(2, 100f), Player(3, 300f, inCar: true) };
        Assert.That(ItemSimulatorChoice.ForWake(all, 0, Range, out byte chosen), Is.True);
        Assert.That(chosen, Is.EqualTo(3), "a rider, out of range but in the car");
        Assert.That(ItemSimulatorChoice.ForWake(all.Take(2), 0, Range, out chosen), Is.True);
        Assert.That(chosen, Is.EqualTo(2), "a player in range before the server");
        Assert.That(ItemSimulatorChoice.ForWake(all.Take(1), 0, Range, out chosen), Is.True);
        Assert.That(chosen, Is.EqualTo(0), "the server itself, with nobody else");
    }

    [Test]
    public void ThePreferredIdIsNeverTheServerByAccident()
    {
        // Preferred 0 is "nobody in particular": a player in range still comes before the server.
        Assert.That(ItemSimulatorChoice.ForWake(new[] { Server(), Player(2, 100f) }, 0, Range, out byte chosen), Is.True);
        Assert.That(chosen, Is.EqualTo(2));
    }

    [Test]
    public void NobodyWithTheItemIsNoWake()
    {
        Assert.That(ItemSimulatorChoice.ForWake(new[] { Player(2, 10f, hasItem: false) }, 2, Range, out _), Is.False);
    }

    [Test]
    public void ThePlayersInRangeWithoutItAreSentIt()
    {
        SimulatorCandidate[] all = { Server(solid: false), Player(2, 100f, hasItem: false), Player(3, 300f, hasItem: false, inCar: true),
            Player(4, 500f, hasItem: false), Player(5, 10f) };
        Assert.That(ItemSimulatorChoice.Lacking(all, Range), Is.EqualTo(new byte[] { 3, 2 }));
    }
}
