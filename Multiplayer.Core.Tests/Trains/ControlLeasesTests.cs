using Multiplayer.Core.Trains;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Trains;

[TestFixture]
public sealed class ControlLeasesTests
{
    private const uint Throttle = 1, Brake = 2;

    [Test]
    public void Take_GrantsAFreeControlInRange()
    {
        ControlLeases<string> leases = new();

        Assert.That(leases.Take(Throttle, "a", inRange: true), Is.EqualTo(LeaseOutcome.Granted));
        Assert.That(leases.HolderOf(Throttle), Is.EqualTo("a"));
        Assert.That(leases.Holds("a", Throttle), Is.True);
        Assert.That(leases.Count, Is.EqualTo(1));
    }

    [Test]
    public void Take_RefusesAFreeControlOutOfRange()
    {
        ControlLeases<string> leases = new();

        Assert.That(leases.Take(Throttle, "a", inRange: false), Is.EqualTo(LeaseOutcome.OutOfRange));
        Assert.That(leases.HolderOf(Throttle), Is.Null);
    }

    [Test]
    public void Take_RefusesAControlAnotherPlayerHolds_WhereverTheyAre()
    {
        ControlLeases<string> leases = new();
        leases.Take(Throttle, "a", inRange: true);

        Assert.That(leases.Take(Throttle, "b", inRange: true), Is.EqualTo(LeaseOutcome.HeldByOther));
        Assert.That(leases.Take(Throttle, "b", inRange: false), Is.EqualTo(LeaseOutcome.HeldByOther));
        Assert.That(leases.HolderOf(Throttle), Is.EqualTo("a"));
    }

    [Test]
    public void Take_AgainByTheHolder_ChangesNothing()
    {
        ControlLeases<string> leases = new();
        leases.Take(Throttle, "a", inRange: true);

        // A holder that has since moved off keeps it: the range is only checked to take one.
        Assert.That(leases.Take(Throttle, "a", inRange: false), Is.EqualTo(LeaseOutcome.AlreadyHeld));
        Assert.That(leases.HolderOf(Throttle), Is.EqualTo("a"));
    }

    [Test]
    public void Release_ByTheHolderFreesIt_ByAnyoneElseDoesNot()
    {
        ControlLeases<string> leases = new();
        leases.Take(Throttle, "a", inRange: true);

        Assert.That(leases.Release(Throttle, "b"), Is.EqualTo(LeaseOutcome.NotOwner));
        Assert.That(leases.HolderOf(Throttle), Is.EqualTo("a"));
        Assert.That(leases.Release(Throttle, "a"), Is.EqualTo(LeaseOutcome.Released));
        Assert.That(leases.HolderOf(Throttle), Is.Null);
        Assert.That(leases.Release(Throttle, "a"), Is.EqualTo(LeaseOutcome.NotHeld));
    }

    [Test]
    public void MayWrite_WhenNobodyElseHoldsAnyOfThePorts()
    {
        ControlLeases<string> leases = new();
        leases.Take(Throttle, "a", inRange: true);

        Assert.That(leases.MayWrite("a", new[] { Throttle, Brake }), Is.True);
        // An unheld control is anyone's: a VR valve or a cab window takes no lease.
        Assert.That(leases.MayWrite("b", new[] { Brake }), Is.True);
        Assert.That(leases.MayWrite("b", new[] { Throttle, Brake }), Is.False);
        Assert.That(leases.MayWrite(null, new[] { Brake }), Is.False);
        Assert.That(leases.MayWrite("a", null), Is.False);
    }

    [Test]
    public void ReleaseAll_FreesOnlyThatPlayersLeases()
    {
        ControlLeases<string> leases = new();
        leases.Take(Throttle, "a", inRange: true);
        leases.Take(Brake, "b", inRange: true);

        Assert.That(leases.ReleaseAll("a"), Is.EquivalentTo(new[] { Throttle }));
        Assert.That(leases.HolderOf(Throttle), Is.Null);
        Assert.That(leases.HolderOf(Brake), Is.EqualTo("b"));
        Assert.That(leases.ReleaseAll("a"), Is.Empty);
    }
}
