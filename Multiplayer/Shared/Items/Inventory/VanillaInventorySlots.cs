using System.Collections.Generic;
using System.Reflection;
using DV.InventorySystem;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// Vanilla's own inventory data, reached through its private members (DV.Inventory is
/// publicized), for D14's stand-ins (docs/design/items/inventory.md, "Stand-ins"). Vanilla keys its
/// slots, its dropped and active sets, its claims and its hands by the GameObject. An item's object
/// and its stand-in swap places here (<see cref="Rebind"/>) before vanilla acts on either, so
/// vanilla, its UI and every listener of its events see one consistent object for the slot.
/// </summary>
internal static class VanillaInventorySlots
{
    /// <summary>
    /// Puts <paramref name="to"/> wherever vanilla's data has <paramref name="from"/>: its slot
    /// (with the slot's flags), its dropped or active set, its claim and its hand. No event fires:
    /// the slot looks the same. False when vanilla has nothing of <paramref name="from"/>.
    /// </summary>
    public static bool Rebind(DV.InventorySystem.Inventory inventory, GameObject from, GameObject to)
    {
        if (inventory == null || from == null || to == null || from == to)
            return false;
        bool found = false;
        InventoryItemData[] slots = inventory.InventoryItems;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] != null && slots[i].item == from)
            {
                slots[i].item = to;
                found = true;
            }
        found |= Swap(inventory.droppedItems, from, to);
        found |= Swap(inventory.inventoryItemsExcludingDropped, from, to);
        if (inventory.lockedAndReservedMap.TryGetValue(from, out int claimed))
        {
            inventory.lockedAndReservedMap.Remove(from);
            inventory.lockedAndReservedMap[to] = claimed;
            found = true;
        }
        GameObject[] hands = inventory.EquippedItems;
        for (int i = 0; i < hands.Length; i++)
            if (hands[i] == from)
            {
                hands[i] = to;
                found = true;
            }
        // VR parks a stashed object at the end of the frame: the object leaving the slot isn't parked.
        if (inventory.removeItemFromWorldCoros.TryGetValue(from, out Coroutine parking))
        {
            if (parking != null)
                inventory.StopCoroutine(parking);
            inventory.removeItemFromWorldCoros.Remove(from);
        }
        return found;
    }

    private static bool Swap(HashSet<GameObject> set, GameObject from, GameObject to)
    {
        if (!set.Remove(from))
            return false;
        set.Add(to);
        return true;
    }

    /// <summary>Whether vanilla's data names this object anywhere: a slot, a set, a claim or a hand.</summary>
    public static bool Holds(DV.InventorySystem.Inventory inventory, GameObject item)
    {
        if (inventory == null || item == null)
            return false;
        foreach (InventoryItemData slot in inventory.InventoryItems)
            if (slot != null && slot.item == item)
                return true;
        return inventory.droppedItems.Contains(item) || inventory.inventoryItemsExcludingDropped.Contains(item) ||
               inventory.lockedAndReservedMap.ContainsKey(item) || inventory.GetEquipSlotForItem(item) >= 0;
    }

    /// <summary>
    /// Takes an object out of vanilla's data entirely, its slot emptied as a purge, so the UI and
    /// every listener redraw the slot. Never touches the object itself. The slot it was in, or -1.
    /// </summary>
    public static int Forget(DV.InventorySystem.Inventory inventory, GameObject item)
    {
        if (inventory == null || item == null)
            return -1;
        int slot = inventory.IndexOf(item);
        InventoryItemData[] slots = inventory.InventoryItems;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] != null && slots[i].item == item)
                slots[i] = InventoryItemData.Empty;
        inventory.droppedItems.Remove(item);
        inventory.inventoryItemsExcludingDropped.Remove(item);
        inventory.lockedAndReservedMap.Remove(item);
        GameObject[] hands = inventory.EquippedItems;
        for (int i = 0; i < hands.Length; i++)
            if (hands[i] == item)
                hands[i] = null;
        if (slot >= 0)
            Fire(inventory, new InventorySlotState(slot, item, InventoryItemState.None, false, false, -1),
                InventoryActionType.Purge);
        return slot;
    }

    // Vanilla raises its own event only from inside Inventory: its backing delegate is read here.
    private static readonly FieldInfo statusChanged = AccessTools.Field(typeof(DV.InventorySystem.Inventory),
        nameof(DV.InventorySystem.Inventory.InventoryStatusChanged));

    /// <summary>
    /// Raises vanilla's InventoryStatusChanged for one slot, as vanilla itself would; one listener at
    /// a time. A listener on an object going this frame can throw (an item's own position handler, as
    /// it's destroyed): it skipped the others, and the caller's own clean-up (NetworkedItem.OnDestroy).
    /// </summary>
    public static void Fire(DV.InventorySystem.Inventory inventory, InventorySlotState state, InventoryActionType action)
    {
        if (statusChanged?.GetValue(inventory) is not DV.InventorySystem.Inventory.InventoryStatusChangedDelegate listeners)
            return;
        foreach (DV.InventorySystem.Inventory.InventoryStatusChangedDelegate listener in listeners.GetInvocationList())
        {
            try
            {
                listener(state, action, InventorySlotState.Empty, InventoryActionType.None);
            }
            catch (System.Exception exception)
            {
                Multiplayer.LogWarning($"Inventory: {listener.Method.DeclaringType?.Name} threw on a slot's " +
                                       $"{action}: {exception.Message}");
            }
        }
    }

    /// <summary>The slot vanilla keeps this object in, dropped or not, or -1: its own data, not <c>IndexOf</c> (which answers for silhouettes too).</summary>
    public static int SlotOf(DV.InventorySystem.Inventory inventory, GameObject item)
    {
        if (inventory == null || item == null)
            return -1;
        InventoryItemData[] slots = inventory.InventoryItems;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] != null && slots[i].item == item)
                return i;
        return -1;
    }

    /// <summary>The item vanilla keeps in a slot, dropped or not, or null.</summary>
    public static GameObject At(DV.InventorySystem.Inventory inventory, int slot) =>
        inventory == null || slot < 0 || slot >= inventory.InventoryItems.Length ? null : inventory.InventoryItems[slot]?.item;
}
