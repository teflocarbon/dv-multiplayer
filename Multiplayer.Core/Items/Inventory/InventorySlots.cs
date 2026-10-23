using System;
using System.Collections.Generic;

namespace Multiplayer.Core.Items.Inventory;

/// <summary>
/// A player's inventory slots as the server keeps them (docs/design/items/inventory.md, "Slots"):
/// Derail Valley's 36, the last three of which are the VR belt.
/// </summary>
public static class InventorySlots
{
    public const int Capacity = 36;
    public const int FirstBeltSlot = 33;

    public static bool IsValid(int slot, int capacity = Capacity) => slot >= 0 && slot < capacity;
    public static bool IsBelt(int slot) => slot >= FirstBeltSlot && slot < Capacity;

    /// <summary>
    /// The slot a new essential's claim takes (OI-ESS-01, -02, -03): the slot asked for if no claim
    /// and no other item holds it, else the lowest slot off the belt that neither a claim nor an
    /// item holds; -1 when there is none. The issuer and the ledger both ask this, so they agree.
    /// </summary>
    public static int ChooseClaim(int preferred, ICollection<int> claimed, Func<int, bool> occupied,
        int capacity = Capacity)
    {
        bool Free(int slot) => !claimed.Contains(slot) && (occupied == null || !occupied(slot));
        if (IsValid(preferred, capacity) && !IsBelt(preferred) && Free(preferred))
            return preferred;
        for (int slot = 0; slot < Math.Min(capacity, FirstBeltSlot); slot++)
            if (Free(slot))
                return slot;
        return -1;
    }

    /// <summary>The lowest free slot for an ordinary item, belt last; -1 when the inventory is full.</summary>
    public static int FirstFree(Func<int, bool> taken, int capacity = Capacity)
    {
        for (int slot = 0; slot < capacity; slot++)
            if (!taken(slot))
                return slot;
        return -1;
    }
}
