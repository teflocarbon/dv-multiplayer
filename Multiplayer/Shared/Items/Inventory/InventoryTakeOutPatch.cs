using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// The local player taking an item out of a slot (docs/design/items/inventory.md, "Taking out";
/// D14, OI-COLD-10): before vanilla acts on a stand-in, its object is made from the row in the same
/// frame and swapped in (<see cref="IInventoryRowHolder.TakeOut"/>, on a client or for a player host's own player). The hotbar's unstash peeks the
/// slot before it equips, so it's taken out before the peek; the UI's equip and the VR backpack
/// grab go through <c>EquipItem</c>; the UI's drop through <c>DropItemFromHandsOrInventory</c>. A
/// move onto an enabled VR belt slot takes the item out (the belt holds objects, OI-COLD-14), and
/// one off it puts the object away again.
/// </summary>
[HarmonyPatch]
internal static class InventoryTakeOutPatch
{
    private static InventoryStandIn StandIn(GameObject item) => item != null ? item.GetComponent<InventoryStandIn>() : null;

    // A silhouette isn't taken out: its item is elsewhere (InventorySilhouettes).
    private static GameObject TakeOut(InventoryStandIn standIn, string reason) =>
        InventorySilhouettes.IsSilhouette(standIn) ? null : InventoryRowHolder.Current?.TakeOut(standIn, reason)?.gameObject;

    [HarmonyPatch(typeof(GrabberStashingHandler), nameof(GrabberStashingHandler.UnStash))]
    [HarmonyPrefix]
    private static void BeforeUnStash(GrabberStashingHandler __instance)
    {
        DV.InventorySystem.Inventory inventory = DV.InventorySystem.Inventory.Instance;
        if (inventory == null || __instance.hotbarController == null)
            return;
        if (StandIn(inventory.PeekItemAtSlot(__instance.hotbarController.SelectedSlot, false)) is { } standIn)
            TakeOut(standIn, "hotbar-unstash");
    }

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), nameof(DV.InventorySystem.Inventory.EquipItem),
        new[] { typeof(GameObject), typeof(int), typeof(int) })]
    [HarmonyPrefix]
    private static bool BeforeEquip(ref GameObject item, ref int __result)
    {
        if (StandIn(item) is not { } standIn)
            return true;
        GameObject taken = TakeOut(standIn, "equip");
        if (taken != null)
        {
            item = taken;
            return true;
        }
        // Not made (no session, no prefab): a hand never holds a stand-in.
        __result = -1;
        return false;
    }

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), nameof(DV.InventorySystem.Inventory.DropItemFromHandsOrInventory),
        new[] { typeof(int), typeof(bool) })]
    [HarmonyPrefix]
    private static void BeforeDrop(DV.InventorySystem.Inventory __instance, int slot, bool keepInactive)
    {
        // keepInactive only empties the slot (the mod's own removals, a container's deposit).
        if (keepInactive || __instance.GetSlotDroppedState(slot))
            return;
        if (StandIn(VanillaInventorySlots.At(__instance, slot)) is { } standIn)
            TakeOut(standIn, "drop");
    }

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), nameof(DV.InventorySystem.Inventory.MoveItemFromTo))]
    [HarmonyPrefix]
    private static void BeforeMove(DV.InventorySystem.Inventory __instance, int originSlot, int targetSlot) =>
        OntoBelt(__instance, originSlot, targetSlot);

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), nameof(DV.InventorySystem.Inventory.MoveItemFromTo))]
    [HarmonyPostfix]
    private static void AfterMove(DV.InventorySystem.Inventory __instance, int targetSlot, bool __result)
    {
        if (__result)
            OffBelt(__instance, targetSlot);
    }

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), nameof(DV.InventorySystem.Inventory.SwapItems))]
    [HarmonyPrefix]
    private static void BeforeSwap(DV.InventorySystem.Inventory __instance, int originSlot, int targetSlot)
    {
        OntoBelt(__instance, originSlot, targetSlot);
        OntoBelt(__instance, targetSlot, originSlot);
    }

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), nameof(DV.InventorySystem.Inventory.SwapItems))]
    [HarmonyPostfix]
    private static void AfterSwap(DV.InventorySystem.Inventory __instance, int originSlot, int targetSlot, bool __result)
    {
        if (!__result)
            return;
        OffBelt(__instance, originSlot);
        OffBelt(__instance, targetSlot);
    }

    // A stand-in going to an enabled VR belt slot is taken out first: vanilla shows it there.
    private static void OntoBelt(DV.InventorySystem.Inventory inventory, int fromSlot, int toSlot)
    {
        if (!inventory.IsValidVRBeltIndex(toSlot) || StandIn(VanillaInventorySlots.At(inventory, fromSlot)) is not { } standIn ||
            !standIn.IsBeltSnappable)
            return;
        TakeOut(standIn, "onto-vr-belt");
    }

    // An object left in a slot that isn't an enabled VR belt slot (moved off the belt) is put away
    // again: the slot move itself is the request, so none is sent.
    private static void OffBelt(DV.InventorySystem.Inventory inventory, int slot)
    {
        GameObject gameObject = VanillaInventorySlots.At(inventory, slot);
        IInventoryRowHolder holder = InventoryRowHolder.Current;
        NetworkedItem item = gameObject != null ? gameObject.GetComponent<NetworkedItem>() : null;
        if (holder == null || item == null || inventory.GetSlotDroppedState(slot) ||
            inventory.IsValidVRBeltIndex(slot) && item.Item.IsBeltSnappable || !holder.PutsAwayAsStandIn(item))
            return;
        holder.StowWithoutRequest(item);
    }
}
