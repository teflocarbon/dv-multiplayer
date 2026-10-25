using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Physics;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items.Physics;

[TestFixture]
public sealed class ItemLeaseRulesTests
{
    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.StaticParent)]
    public void LooseItemsAreSimulated(LivePlacement placement) =>
        Assert.That(ItemLeaseRules.IsSimulatable(placement), Is.True);

    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.PlayerInventory)]
    [TestCase(LivePlacement.Container)]
    [TestCase(LivePlacement.LostAndFound)]
    [TestCase(LivePlacement.Installed)]
    [TestCase(LivePlacement.Destroyed)]
    [TestCase(LivePlacement.Attached)]
    [TestCase(LivePlacement.SnappedAttachment)]
    [TestCase(LivePlacement.Magazine)]
    public void NothingElseIs(LivePlacement placement) =>
        Assert.That(ItemLeaseRules.IsSimulatable(placement), Is.False);

    [Test]
    public void ALeaseHoldsAtItsOwnRevision() =>
        Assert.That(ItemLeaseRules.Holds(LivePlacement.World, 7, 7, 0, 0), Is.True);

    [Test]
    public void ALeaseHoldsWhileOnlyObjectStateChanged() =>
        // r7 granted; r8 and r9 changed only a lantern's flame.
        Assert.That(ItemLeaseRules.Holds(LivePlacement.World, 7, 9, 7, 9), Is.True);

    [Test]
    public void APlacementChangeSinceEndsIt() =>
        // r8 moved it (a Lost and Found collection mid-flight: still "World" to a stale reader).
        Assert.That(ItemLeaseRules.Holds(LivePlacement.World, 7, 8, 0, 0), Is.False);

    [Test]
    public void ARecalledItemsLeaseDoesntHold() =>
        // SL4: recalled out of a moving cab into the inventory; the lease's revision is older.
        Assert.That(ItemLeaseRules.Holds(LivePlacement.PlayerInventory, 7, 8, 0, 0), Is.False);

    [Test]
    public void ALeaseDoesntHoldForAnItemThatIsntLoose() =>
        // Even at its own revision (no record should be there, but nothing saves its pose if it is).
        Assert.That(ItemLeaseRules.Holds(LivePlacement.LostAndFound, 7, 7, 0, 0), Is.False);

    [Test]
    public void ObjectStateKeepsTheLease() =>
        Assert.That(ItemLeaseRules.AfterChange(false, LivePlacement.World, false), Is.EqualTo(LeaseAfterChange.Keeps));

    [TestCase(LivePlacement.World)]
    [TestCase(LivePlacement.TrainInterior)]
    [TestCase(LivePlacement.StaticParent)]
    public void AReleaseBeginsOne(LivePlacement placement) =>
        Assert.That(ItemLeaseRules.AfterChange(true, placement, true), Is.EqualTo(LeaseAfterChange.Begins));

    [TestCase(LivePlacement.PlayerHand)]
    [TestCase(LivePlacement.PlayerInventory)]
    [TestCase(LivePlacement.Container)]
    [TestCase(LivePlacement.LostAndFound)]
    [TestCase(LivePlacement.Installed)]
    [TestCase(LivePlacement.Magazine)]
    public void EveryOtherPlaceEndsIt(LivePlacement placement)
    {
        Assert.That(ItemLeaseRules.AfterChange(true, placement, false), Is.EqualTo(LeaseAfterChange.Ends));
        Assert.That(ItemLeaseRules.AfterChange(true, placement, true), Is.EqualTo(LeaseAfterChange.Ends));
    }

    [Test]
    public void TheServerMovingItAboutTheWorldEndsIt() =>
        // Not a player's release (a deleted car's item put beside the track): the old lease is stale.
        Assert.That(ItemLeaseRules.AfterChange(true, LivePlacement.World, false), Is.EqualTo(LeaseAfterChange.Ends));
}
