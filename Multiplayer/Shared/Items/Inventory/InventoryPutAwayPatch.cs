using System;
using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// The local player putting an object away (docs/design/items/inventory.md, "Putting away"; D14,
/// OI-COLD-10): a hand's item into the inventory (vanilla's unequip with add, the hotbar's stash) or
/// an object added straight in. Before vanilla adds it, the object and its stand-in swap places in
/// vanilla's data, so vanilla adds the stand-in, its events carry the stand-in and nothing parks
/// the object. Once it's in its slot the server is asked for the stash and the object goes
/// (<see cref="IInventoryRowHolder.CommitPutAway"/>), or on the host the stash is committed there; if
/// vanilla didn't add it, the swap is undone. A VR belt slot keeps the object, as a hand does
/// (OI-COLD-14). The same on a client and for a player host's own player (<see cref="InventoryRowHolder"/>).
/// </summary>
[HarmonyPatch(typeof(DV.InventorySystem.Inventory))]
internal static class InventoryPutAwayPatch
{
    internal sealed class PutAway
    {
        public NetworkedItem Item;
        public InventoryStandIn StandIn;
    }

    private static int notPutAway;

    /// <summary>
    /// An add of the mod's own that isn't a put-away: an essential's silhouette, made by adding the
    /// object to its slot and dropping it (InventoryIntegration.EnsureDroppedClaim). As a put-away
    /// it became a stand-in and a stash the server refused (the item is in Lost and Found), and the
    /// silhouette was gone.
    /// </summary>
    internal static T NotAPutAway<T>(Func<T> add)
    {
        notPutAway++;
        try
        {
            return add();
        }
        finally
        {
            notPutAway--;
        }
    }

    [HarmonyPatch("UnequipItem", new[] { typeof(bool), typeof(int), typeof(bool), typeof(InventoryActionType), typeof(InventorySlotState) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
    [HarmonyPrefix]
    private static void BeforeUnequip(DV.InventorySystem.Inventory __instance, bool addToInventory, int equippedSlot, out PutAway __state)
    {
        __state = null;
        if (!addToInventory)
            return;
        GameObject held = __instance.GetEquippedItemAtSlot(equippedSlot);
        if (held == null)
            return;
        int slot = __instance.FindReservedSlotForDroppedItem(held);
        if (slot < 0)
            slot = __instance.GetFirstFreeSlot();
        __state = Begin(__instance, held, slot);
    }

    [HarmonyPatch("UnequipItem", new[] { typeof(bool), typeof(int), typeof(bool), typeof(InventoryActionType), typeof(InventorySlotState) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
    [HarmonyPostfix]
    private static void AfterUnequip(DV.InventorySystem.Inventory __instance, PutAway __state) => End(__instance, __state);

    [HarmonyPatch("AddItemToInventory", new[] { typeof(GameObject), typeof(int), typeof(bool), typeof(bool) })]
    [HarmonyPrefix]
    private static void BeforeAdd(DV.InventorySystem.Inventory __instance, ref GameObject item, int slot, bool addAsDropped,
        out PutAway __state)
    {
        __state = null;
        // A held object is added through the unequip above; a silhouette isn't a put-away. The host
        // puts away what it adds directly once it commits it (IInventoryRowHolder.CatchesDirectAdds).
        if (InventoryRowHolder.Current?.CatchesDirectAdds != true || addAsDropped || notPutAway > 0 || item == null ||
            __instance.GetEquipSlotForItem(item) >= 0)
            return;
        __state = Begin(__instance, item, slot);
        if (__state != null)
            item = __state.StandIn.gameObject;
    }

    [HarmonyPatch("AddItemToInventory", new[] { typeof(GameObject), typeof(int), typeof(bool), typeof(bool) })]
    [HarmonyPostfix]
    private static void AfterAdd(DV.InventorySystem.Inventory __instance, PutAway __state) => End(__instance, __state);

    private static PutAway Begin(DV.InventorySystem.Inventory inventory, GameObject gameObject, int slot)
    {
        IInventoryRowHolder holder = InventoryRowHolder.Current;
        NetworkedItem item = gameObject.GetComponent<NetworkedItem>();
        if (holder == null || slot < 0 || !holder.PutsAwayAsStandIn(item) || inventory.Contains(gameObject, false) ||
            inventory.IsValidVRBeltIndex(slot) && item.Item.IsBeltSnappable)
            return null;
        InventoryStandIn standIn = InventoryStandIn.ForObject(item);
        VanillaInventorySlots.Rebind(inventory, gameObject, standIn.gameObject);
        return new PutAway { Item = item, StandIn = standIn };
    }

    private static void End(DV.InventorySystem.Inventory inventory, PutAway putAway)
    {
        if (putAway == null)
            return;
        if (putAway.StandIn != null && inventory.Contains(putAway.StandIn.gameObject, false))
        {
            InventoryRowHolder.Current?.CommitPutAway(putAway.Item, putAway.StandIn);
            return;
        }
        // Vanilla didn't take it (a full inventory): the object is where it was.
        if (putAway.StandIn != null)
        {
            VanillaInventorySlots.Rebind(inventory, putAway.StandIn.gameObject, putAway.Item.gameObject);
            putAway.StandIn.Retire();
        }
    }
}
