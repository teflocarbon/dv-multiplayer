namespace Multiplayer.Core.Items;

/// <summary>A read-only projection of one item's relationship to the local inventory.</summary>
public interface IInventoryView
{
    bool IsAvailable { get; }
    int CurrentSlot { get; }
    bool ContainsActiveItem { get; }
    bool IsExpectedSlotOccupiedByOther { get; }
    bool CurrentSlotReserved { get; }
    bool CurrentSlotDropped { get; }
}

public enum InventoryClaimAction
{
    None,
    DropExistingItemInPlace,
    AddToExpectedSlotThenDrop,
    RestoreExistingItem,
    AddToExpectedSlot,
    RejectMissingSlot,
    RejectSlotMismatch,
    RejectSlotOccupied
}

public readonly struct InventoryClaimPlan
{
    public InventoryClaimPlan(InventoryClaimAction action, int targetSlot, string reason)
    {
        Action = action;
        TargetSlot = targetSlot;
        Reason = reason ?? string.Empty;
    }

    public InventoryClaimAction Action { get; }
    public int TargetSlot { get; }
    public string Reason { get; }
    public bool Rejected => Action is InventoryClaimAction.RejectMissingSlot or
        InventoryClaimAction.RejectSlotMismatch or InventoryClaimAction.RejectSlotOccupied;
}

public static class InventoryClaimPlanner
{
    public static InventoryClaimPlan EnsureDroppedClaim(IInventoryView view, int expectedSlot)
    {
        if (view == null || !view.IsAvailable || expectedSlot < 0)
            return Reject(InventoryClaimAction.RejectMissingSlot, expectedSlot, "claim-slot-missing");
        if (view.CurrentSlot >= 0 && view.CurrentSlot != expectedSlot)
            return Reject(InventoryClaimAction.RejectSlotMismatch, view.CurrentSlot, "essential-claim-moved");
        if (view.CurrentSlot >= 0)
        {
            return view.CurrentSlotReserved && view.CurrentSlotDropped
                ? new InventoryClaimPlan(InventoryClaimAction.None, view.CurrentSlot, string.Empty)
                : new InventoryClaimPlan(InventoryClaimAction.DropExistingItemInPlace,
                    view.CurrentSlot, string.Empty);
        }
        if (view.IsExpectedSlotOccupiedByOther)
            return Reject(InventoryClaimAction.RejectSlotOccupied, expectedSlot,
                "essential-claim-slot-occupied");
        return new InventoryClaimPlan(InventoryClaimAction.AddToExpectedSlotThenDrop,
            expectedSlot, string.Empty);
    }

    public static InventoryClaimPlan RestoreClaim(IInventoryView view, int expectedSlot)
    {
        if (view == null || !view.IsAvailable)
            return Reject(InventoryClaimAction.RejectMissingSlot, expectedSlot, "recall-inventory-missing");
        if (view.CurrentSlot >= 0 && expectedSlot >= 0 && view.CurrentSlot != expectedSlot)
            return Reject(InventoryClaimAction.RejectSlotMismatch, view.CurrentSlot,
                "recall-claim-slot-mismatch");

        int targetSlot = view.CurrentSlot >= 0 ? view.CurrentSlot : expectedSlot;
        if (targetSlot < 0)
            return Reject(InventoryClaimAction.RejectMissingSlot, targetSlot,
                "recall-claim-slot-missing");
        if (view.IsExpectedSlotOccupiedByOther)
            return Reject(InventoryClaimAction.RejectSlotOccupied, targetSlot,
                "recall-claim-slot-occupied");
        if (view.ContainsActiveItem)
            return new InventoryClaimPlan(InventoryClaimAction.None, targetSlot, string.Empty);
        return new InventoryClaimPlan(view.CurrentSlot >= 0
                ? InventoryClaimAction.RestoreExistingItem
                : InventoryClaimAction.AddToExpectedSlot,
            targetSlot, string.Empty);
    }

    private static InventoryClaimPlan Reject(InventoryClaimAction action, int slot, string reason) =>
        new(action, slot, reason);
}
