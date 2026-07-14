using Multiplayer.Core.Items;
using NUnit.Framework;

namespace Multiplayer.Core.Tests.Items;

[TestFixture]
public sealed class InventoryClaimPlannerTests
{
    [Test]
    public void EnsureDropped_AlreadyCorrect_IsNoOp()
    {
        FakeInventory view = new() { CurrentSlot = 3, Reserved = true, Dropped = true };
        Assert.That(InventoryClaimPlanner.EnsureDroppedClaim(view, 3).Action,
            Is.EqualTo(InventoryClaimAction.None));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void EnsureDropped_ExistingActiveOrIncompleteClaim_DropsInPlace(bool reserved, bool dropped)
    {
        FakeInventory view = new() { CurrentSlot = 3, Reserved = reserved, Dropped = dropped };
        Assert.That(InventoryClaimPlanner.EnsureDroppedClaim(view, 3).Action,
            Is.EqualTo(InventoryClaimAction.DropExistingItemInPlace));
    }

    [Test]
    public void EnsureDropped_MissingObject_ReconstructsAtExpectedSlotThenDrops()
    {
        FakeInventory view = new() { CurrentSlot = -1 };
        InventoryClaimPlan plan = InventoryClaimPlanner.EnsureDroppedClaim(view, 3);
        Assert.That(plan.Action, Is.EqualTo(InventoryClaimAction.AddToExpectedSlotThenDrop));
        Assert.That(plan.TargetSlot, Is.EqualTo(3));
    }

    [Test]
    public void EnsureDropped_NeverMovesAnExistingClaim()
    {
        FakeInventory view = new() { CurrentSlot = 4, Reserved = true, Dropped = true };
        InventoryClaimPlan plan = InventoryClaimPlanner.EnsureDroppedClaim(view, 3);
        Assert.That(plan.Action, Is.EqualTo(InventoryClaimAction.RejectSlotMismatch));
        Assert.That(plan.Reason, Is.EqualTo("essential-claim-moved"));
    }

    [Test]
    public void EnsureDropped_DoesNotOverwriteAnotherSlotOccupant()
    {
        FakeInventory view = new() { CurrentSlot = -1, OccupiedByOther = true };
        Assert.That(InventoryClaimPlanner.EnsureDroppedClaim(view, 3).Action,
            Is.EqualTo(InventoryClaimAction.RejectSlotOccupied));
    }

    [Test]
    public void Restore_ActiveExistingItem_IsNoOp()
    {
        FakeInventory view = new() { CurrentSlot = 3, Active = true };
        Assert.That(InventoryClaimPlanner.RestoreClaim(view, 3).Action,
            Is.EqualTo(InventoryClaimAction.None));
    }

    [Test]
    public void Restore_DroppedExistingItem_ReactivatesSameEntry()
    {
        FakeInventory view = new() { CurrentSlot = 3, Active = false, Dropped = true, Reserved = true };
        Assert.That(InventoryClaimPlanner.RestoreClaim(view, 3).Action,
            Is.EqualTo(InventoryClaimAction.RestoreExistingItem));
    }

    [Test]
    public void Restore_MissingItem_AddsAtExpectedSlot()
    {
        FakeInventory view = new() { CurrentSlot = -1 };
        InventoryClaimPlan plan = InventoryClaimPlanner.RestoreClaim(view, 3);
        Assert.That(plan.Action, Is.EqualTo(InventoryClaimAction.AddToExpectedSlot));
        Assert.That(plan.TargetSlot, Is.EqualTo(3));
    }

    [Test]
    public void Restore_RejectsMovedOrOccupiedClaim()
    {
        Assert.That(InventoryClaimPlanner.RestoreClaim(
            new FakeInventory { CurrentSlot = 4 }, 3).Action,
            Is.EqualTo(InventoryClaimAction.RejectSlotMismatch));
        Assert.That(InventoryClaimPlanner.RestoreClaim(
            new FakeInventory { CurrentSlot = -1, OccupiedByOther = true }, 3).Action,
            Is.EqualTo(InventoryClaimAction.RejectSlotOccupied));
    }

    [Test]
    public void MissingInventoryOrSlot_IsRejected()
    {
        Assert.That(InventoryClaimPlanner.EnsureDroppedClaim(
            new FakeInventory { Available = false }, 3).Rejected, Is.True);
        Assert.That(InventoryClaimPlanner.RestoreClaim(
            new FakeInventory(), -1).Action, Is.EqualTo(InventoryClaimAction.RejectMissingSlot));
    }

    private sealed class FakeInventory : IInventoryView
    {
        public bool Available { get; set; } = true;
        public bool IsAvailable => Available;
        public int CurrentSlot { get; set; } = -1;
        public bool Active { get; set; }
        public bool ContainsActiveItem => Active;
        public bool OccupiedByOther { get; set; }
        public bool IsExpectedSlotOccupiedByOther => OccupiedByOther;
        public bool Reserved { get; set; }
        public bool CurrentSlotReserved => Reserved;
        public bool Dropped { get; set; }
        public bool CurrentSlotDropped => Dropped;
    }
}
