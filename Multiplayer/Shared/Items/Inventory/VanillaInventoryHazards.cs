using DV.InventorySystem;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// Vanilla's own inventory bookkeeping, mended where it could hide or lose an item (OI-ESS-13,
/// docs/design/items/inventory.md, "Vanilla's hazards"). Every game runs these: the slots are the
/// server's, but each game's vanilla inventory is the view its player acts on.
/// </summary>
[HarmonyPatch(typeof(DV.InventorySystem.Inventory))]
internal static class VanillaInventoryHazards
{
    /// <summary>
    /// H1: <c>SwapItems</c> moves two slots' data but not vanilla's claim map (reserved and locked
    /// slots, by the object), so a purge after a swap blanked the other item's slot. The map follows
    /// the swap, before anything else reads it.
    /// </summary>
    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.SwapItems))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    private static void AfterSwap(DV.InventorySystem.Inventory __instance, int originSlot, int targetSlot, bool __result)
    {
        if (!__result)
            return;
        Reclaim(__instance, originSlot);
        Reclaim(__instance, targetSlot);
    }

    // Vanilla's own UpdateLockedAndReservedMap, for one slot.
    private static void Reclaim(DV.InventorySystem.Inventory inventory, int slot)
    {
        if (inventory.InventoryItems[slot] is not { item: { } item } data)
            return;
        if (data.IsLocked || data.IsReserved)
            inventory.lockedAndReservedMap[item] = slot;
        else
            inventory.lockedAndReservedMap.Remove(item);
    }

    /// <summary>
    /// H4-H6: a lock toggled on a slot whose object is gone (destroyed while vanilla still named it)
    /// read the dead object and threw halfway, the slot half changed. Its entry goes instead, and
    /// the toggle with it.
    /// </summary>
    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.ToggleSlotLock))]
    [HarmonyPrefix]
    private static bool BeforeToggleLock(DV.InventorySystem.Inventory __instance, int slot, ref bool __result)
    {
        if (slot < 0 || slot >= __instance.InventoryItems.Length || __instance.InventoryItems[slot] is not { } data ||
            !IsDead(data.item))
            return true;
        GameObject dead = data.item;
        __instance.InventoryItems[slot] = InventoryItemData.Empty;
        __instance.droppedItems.Remove(dead);
        __instance.inventoryItemsExcludingDropped.Remove(dead);
        __instance.lockedAndReservedMap.Remove(dead);
        // As vanilla empties a slot a move leaves: a purge with no item.
        VanillaInventorySlots.Fire(__instance, new InventorySlotState(slot, null, InventoryItemState.None, false, false, -1),
            InventoryActionType.Purge);
        __result = false;
        return false;
    }

    /// <summary>
    /// H7: vanilla purges a destroyed item only when it has a claim (its purge reads the claim map),
    /// so an ordinary one destroyed in a slot or a hand stayed in vanilla's sets: a dead entry
    /// counted against the 36 slots, and a full-looking inventory refused an item. Called as an
    /// item's object goes; nothing when vanilla doesn't name it.
    /// </summary>
    internal static void ForgetDestroyed(GameObject item)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (inventory != null && VanillaInventorySlots.Holds(inventory, item))
            VanillaInventorySlots.Forget(inventory, item);
    }

    // Destroyed but still referenced: Unity's null, not the CLR's.
    private static bool IsDead(GameObject item) => !ReferenceEquals(item, null) && item == null;
}
