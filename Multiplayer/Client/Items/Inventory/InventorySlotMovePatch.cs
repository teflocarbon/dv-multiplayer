using HarmonyLib;
using Multiplayer.Shared.Items.Inventory;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using UnityEngine;

namespace Multiplayer.Client.Items.Inventory;

/// <summary>
/// The local player moving an item to another slot of their inventory, or swapping two
/// (docs/design/items/inventory.md, "Moving between slots"): vanilla's move has happened here, and
/// the server is asked for it, since the slots are the ledger's. A refusal comes back as the
/// item's update, which puts it back. The mod's own placements (an update applied, room made for
/// a claim) are the server's already and aren't sent.
/// </summary>
[HarmonyPatch(typeof(DV.InventorySystem.Inventory))]
internal static class InventorySlotMovePatch
{
    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.MoveItemFromTo))]
    [HarmonyPostfix]
    private static void AfterMove(DV.InventorySystem.Inventory __instance, int originSlot, int targetSlot, bool __result)
    {
        if (__result)
            Request(__instance, targetSlot);
    }

    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.SwapItems))]
    [HarmonyPostfix]
    private static void AfterSwap(DV.InventorySystem.Inventory __instance, int originSlot, int targetSlot, bool __result)
    {
        // One request: the item now in the target slot goes there, and the server swaps the other.
        if (__result)
            Request(__instance, targetSlot);
    }

    private static void Request(DV.InventorySystem.Inventory inventory, int slot)
    {
        if (InventoryIntegration.AuthoritativePlacementActive || !InventoryIntegration.RuntimeActive ||
            InventoryIntegration.LocalPlayerId == 0 || inventory != DV.InventorySystem.Inventory.Instance)
            return;
        GameObject moved = inventory.PeekItemAtSlot(slot, false);
        if (moved == null)
            return;
        // A stand-in (D14) is the row: its NetId and revision are the item's.
        InventoryStandIn standIn = moved.GetComponent<InventoryStandIn>();
        NetworkedItem item = standIn == null ? moved.GetComponent<NetworkedItem>() : null;
        ushort netId = standIn != null ? standIn.NetId : item?.NetId ?? 0;
        if (netId == 0)
            return;
        ItemClient.Current?.SendItemRequest(new ItemRequest
        {
            ItemNetId = netId,
            ExpectedRevision = standIn != null ? standIn.Revision : item.AuthorityRevision,
            Action = ItemAction.MoveSlot,
            Slot = slot
        });
        // The stand-in expects the revision its move commits at, as a put-away does: a row sent before
        // the move (InventoryRows.IsLate) doesn't put it back, and a refusal's correction sets it back.
        if (standIn != null)
            standIn.Revision++;
    }
}
