using Multiplayer.Core.Replication;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Replication;

[TestFixture]
public sealed class InterestMembershipTests
{
    [TestCase(false, 10000d, RelevanceAction.Enter)]
    [TestCase(true, 10000d, RelevanceAction.Refresh)]
    [TestCase(true, 10000.01d, RelevanceAction.Retain)]
    public void DistanceBoundaryIsInclusive(bool current, double distance, RelevanceAction expected)
    {
        RelevanceDecision decision = InterestMembershipEvaluator.Evaluate(current, distance, 10000, 1, 3);
        Assert.That(decision.Action, Is.EqualTo(expected));
    }

    [TestCase(3d, RelevanceAction.Retain, true)]
    [TestCase(3.001d, RelevanceAction.Leave, false)]
    public void RemovalDelayIsStrictlyExceeded(double elapsed, RelevanceAction action, bool relevant)
    {
        RelevanceDecision decision = InterestMembershipEvaluator.Evaluate(true, 10001, 10000, elapsed, 3);
        Assert.Multiple(() => {
            Assert.That(decision.Action, Is.EqualTo(action));
            Assert.That(decision.IsRelevant, Is.EqualTo(relevant));
        });
    }

    [Test]
    public void UnknownRelevantItemCreatesAndBecomesKnown()
    {
        ItemDeliveryDecision decision = Delivery(known: false);
        Assert.Multiple(() => {
            Assert.That(decision.Kind, Is.EqualTo(ItemDeliveryKind.Create));
            Assert.That(decision.MarkKnown, Is.True);
            Assert.That(decision.SuppressPayload, Is.False);
        });
    }

    [Test]
    public void SpecialCreateIsSuppressedButStillMarkedKnown()
    {
        ItemDeliveryDecision decision = KnownItemDeliveryEvaluator.Evaluate(true, false, 0, 0, false, true, false);
        Assert.Multiple(() => {
            Assert.That(decision.Kind, Is.EqualTo(ItemDeliveryKind.Create));
            Assert.That(decision.MarkKnown, Is.True);
            Assert.That(decision.SuppressPayload, Is.True);
        });
    }

    [TestCase(9u, 10u, ItemDeliveryKind.FullSync)]
    [TestCase(10u, 10u, ItemDeliveryKind.None)]
    [TestCase(11u, 10u, ItemDeliveryKind.None)]
    public void KnownTickDeterminesCatchUp(uint knownTick, uint dirtyTick, ItemDeliveryKind expected)
    {
        Assert.That(Delivery(known: true, knownTick: knownTick, dirtyTick: dirtyTick).Kind, Is.EqualTo(expected));
    }

    [Test]
    public void CurrentDirtySnapshotTakesPriorityOverCatchUpFullSync()
    {
        ItemDeliveryDecision decision = Delivery(known: true, knownTick: 1, dirtyTick: 20, dirty: true);
        Assert.That(decision.Kind, Is.EqualTo(ItemDeliveryKind.Dirty));
    }

    [TestCase(true, ItemDeliveryKind.Destroy)]
    [TestCase(false, ItemDeliveryKind.None)]
    public void DestroyOnlyTargetsClientsThatKnewItem(bool known, ItemDeliveryKind expected)
    {
        ItemDeliveryDecision decision = KnownItemDeliveryEvaluator.Evaluate(false, known, 0, 0, false, false, true);
        Assert.That(decision.Kind, Is.EqualTo(expected));
    }

    [Test]
    public void DeliveryNeverOccursOutsideInterestUnlessDestroyingKnownItem()
    {
        for (uint knownTick = 0; knownTick < 20; knownTick++)
        for (uint dirtyTick = 0; dirtyTick < 20; dirtyTick++)
        for (int flags = 0; flags < 8; flags++)
        {
            bool known = (flags & 1) != 0;
            bool dirty = (flags & 2) != 0;
            bool suppress = (flags & 4) != 0;
            ItemDeliveryDecision decision = KnownItemDeliveryEvaluator.Evaluate(
                false, known, knownTick, dirtyTick, dirty, suppress, false);
            Assert.That(decision.Kind, Is.EqualTo(ItemDeliveryKind.None));
        }
    }

    private static ItemDeliveryDecision Delivery(bool known, uint knownTick = 0, uint dirtyTick = 0, bool dirty = false) =>
        KnownItemDeliveryEvaluator.Evaluate(true, known, knownTick, dirtyTick, dirty, false, false);
}
