using System.Collections.Generic;

namespace Multiplayer.Core.Items.LostAndFound;

public readonly struct LostItemRetrievalPlan
{
    public LostItemRetrievalPlan(bool accepted, string reason, int targetSlot)
    {
        Accepted = accepted;
        Reason = reason ?? string.Empty;
        TargetSlot = targetSlot;
    }
    public bool Accepted { get; }
    public string Reason { get; }
    public int TargetSlot { get; }
}

public static class LostItemRetrievalPlanner
{
    public static LostItemRetrievalPlan Plan(LostItemRecord record, byte requesterPlayerId,
        uint expectedRevision, int requestedSlot, int existingItemSlot, int inventoryCapacity,
        IReadOnlyCollection<int> occupiedSlots)
    {
        if (record == null || requesterPlayerId == 0)
            return Reject("unknown-lost-item");
        if (record.OwnerPlayerId != requesterPlayerId)
            return Reject("requester-not-persistent-owner");
        if (record.Revision != expectedRevision)
            return Reject("stale-lost-item-revision");
        if (inventoryCapacity <= 0)
            return Reject("inventory-unavailable");

        // Reserved/dropped entries are silhouettes, not genuinely free slots. They cannot be
        // moved by the base game, so restoring the same canonical object must revive that exact
        // slot even though it appears occupied in the inventory summary.
        if (existingItemSlot >= 0 && existingItemSlot < inventoryCapacity)
            return new LostItemRetrievalPlan(true, string.Empty, existingItemSlot);

        HashSet<int> occupied = occupiedSlots == null ? new HashSet<int>() : new HashSet<int>(occupiedSlots);
        if (requestedSlot >= 0 && requestedSlot < inventoryCapacity && !occupied.Contains(requestedSlot))
            return new LostItemRetrievalPlan(true, string.Empty, requestedSlot);
        for (int slot = 0; slot < inventoryCapacity; slot++)
            if (!occupied.Contains(slot))
                return new LostItemRetrievalPlan(true, string.Empty, slot);
        return Reject("inventory-full");
    }

    private static LostItemRetrievalPlan Reject(string reason) => new(false, reason, -1);
}
