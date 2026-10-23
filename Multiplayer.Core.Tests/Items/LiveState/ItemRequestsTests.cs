using System;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LiveState;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.LiveState;

/// <summary>One request, one result (docs/design/items/wire.md): what the server remembers of a request, and how it words a refusal.</summary>
[TestFixture]
public sealed class ItemRequestsTests
{
    [Test]
    public void AFirstRequestIsNew_AndARepeatBeforeItsResultIsPending()
    {
        ItemRequestMemory<string> memory = new();

        Assert.That(memory.Begin(7, out _), Is.EqualTo(ItemRequestStart.New));
        Assert.That(memory.Begin(7, out string remembered), Is.EqualTo(ItemRequestStart.Pending));
        Assert.That(remembered, Is.Null);
    }

    [Test]
    public void ARepeatAfterItsResultGetsTheSameResult_AndDoesNothingTwice()
    {
        ItemRequestMemory<string> memory = new();
        memory.Begin(7, out _);
        memory.Complete(7, "accepted r12");

        Assert.That(memory.Begin(7, out string remembered), Is.EqualTo(ItemRequestStart.Repeat));
        Assert.That(remembered, Is.EqualTo("accepted r12"));
        Assert.That(memory.Count, Is.EqualTo(1));
    }

    [Test]
    public void RequestZeroIsNeverRemembered()
    {
        ItemRequestMemory<string> memory = new();

        Assert.That(memory.Begin(0, out _), Is.EqualTo(ItemRequestStart.New));
        memory.Complete(0, "x");
        Assert.That(memory.Begin(0, out _), Is.EqualTo(ItemRequestStart.New));
        Assert.That(memory.Count, Is.Zero);
    }

    [Test]
    public void AResultForARequestNeverBegunIsIgnored()
    {
        ItemRequestMemory<string> memory = new();
        memory.Complete(9, "stray");

        Assert.That(memory.Begin(9, out _), Is.EqualTo(ItemRequestStart.New));
    }

    [Test]
    public void AnOldRequestPastTheMemoryIsExpired_NeverDoneAgain()
    {
        ItemRequestMemory<string> memory = new(capacity: 2);
        for (uint id = 1; id <= 3; id++)
        {
            memory.Begin(id, out _);
            memory.Complete(id, "r" + id);
        }

        Assert.That(memory.Count, Is.EqualTo(2));
        Assert.That(memory.Begin(1, out _), Is.EqualTo(ItemRequestStart.Expired));
        Assert.That(memory.Begin(3, out string remembered), Is.EqualTo(ItemRequestStart.Repeat));
        Assert.That(remembered, Is.EqualTo("r3"));
        Assert.That(memory.Begin(4, out _), Is.EqualTo(ItemRequestStart.New));
    }

    [Test]
    public void TheMemoryNeedsRoom() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ItemRequestMemory<string>(0));

    [TestCase(null, ItemRefusal.Refused)]
    [TestCase("", ItemRefusal.Refused)]
    [TestCase("recall-busy", ItemRefusal.Busy)]
    [TestCase("unknown-lost-item", ItemRefusal.UnknownItem)]
    [TestCase("lost-item-revision-changed", ItemRefusal.StaleRevision)]
    [TestCase("not-your-item", ItemRefusal.NotYours)]
    [TestCase("take-from-player", ItemRefusal.NotYours)]
    [TestCase("inventory-full", ItemRefusal.NoRoom)]
    [TestCase("item-not-projected", ItemRefusal.NotHere)]
    [TestCase("item-action-invalid", ItemRefusal.Invalid)]
    [TestCase("item-not-recallable", ItemRefusal.NotAllowed)]
    [TestCase("something-else", ItemRefusal.Refused)]
    public void ARefusalIsClassifiedFromItsDetail(string detail, ItemRefusal expected) =>
        Assert.That(ItemRefusals.Classify(detail), Is.EqualTo(expected));

    [Test]
    public void ARelationNamesNoOwner()
    {
        Guid owner = Guid.NewGuid(), other = Guid.NewGuid();
        Assert.Multiple(() =>
        {
            Assert.That(ItemRelations.Of(Guid.Empty, owner), Is.EqualTo(ItemRelation.Nobodys));
            Assert.That(ItemRelations.Of(owner, owner), Is.EqualTo(ItemRelation.Mine));
            Assert.That(ItemRelations.Of(owner, other), Is.EqualTo(ItemRelation.SomeoneElses));
            // A dedicated host's own world has no viewer: every owned item is someone else's.
            Assert.That(ItemRelations.Of(owner, Guid.Empty), Is.EqualTo(ItemRelation.SomeoneElses));
            Assert.That(ItemRelation.Nobodys.IsOwned(), Is.False);
            Assert.That(ItemRelation.SomeoneElses.IsOwned(), Is.True);
        });
    }
}
