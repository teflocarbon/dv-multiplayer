using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// An essential's silhouette under D14 (docs/design/items/inventory.md, "Silhouettes"): its claim
/// slot, dropped and reserved, holds the item's stand-in, not its object, while the item is
/// elsewhere (in the world, in Lost and Found, held by another player). Vanilla routes an item back
/// to its silhouette by the GameObject (its reserved slot, the Get button, an equip), so the object
/// takes the slot back (<see cref="ToObject"/>) just before vanilla acts on it by identity, and gives
/// it to the stand-in again (<see cref="ToStandIn"/>) once vanilla leaves it there dropped and out
/// of the player's hands. While the local player holds the item, the slot is vanilla's own shape.
/// Vanilla's lookups by the object (<c>IndexOf</c>, <c>Contains</c> with dropped entries) answer
/// for its silhouette, so the mod's claim checks read the slot as before. A silhouette lives as long
/// as the object would have kept it: whatever takes the object's claim away takes the stand-in.
/// </summary>
[HarmonyPatch(typeof(DV.InventorySystem.Inventory))]
internal static class InventorySilhouettes
{
    private static int equipping;

    /// <summary>Whether this stand-in is a silhouette: its slot is dropped (the item is elsewhere).</summary>
    public static bool IsSilhouette(DV.InventorySystem.Inventory inventory, InventoryStandIn standIn)
    {
        int slot = VanillaInventorySlots.SlotOf(inventory, standIn != null ? standIn.gameObject : null);
        return slot >= 0 && inventory.InventoryItems[slot].IsDropped;
    }

    /// <summary>Whether this stand-in is a silhouette in the local inventory.</summary>
    public static bool IsSilhouette(InventoryStandIn standIn) =>
        IsSilhouette(DV.InventorySystem.Inventory.Instance, standIn);

    /// <summary>The silhouette stand-in of this object's item, or null.</summary>
    public static InventoryStandIn Of(DV.InventorySystem.Inventory inventory, GameObject item)
    {
        if (inventory == null || item == null || item.GetComponent<InventoryStandIn>() != null ||
            !item.TryGetComponent(out NetworkedItem networked) || networked.NetId == 0 ||
            !InventoryStandIn.TryGet(networked.NetId, out InventoryStandIn standIn))
            return null;
        return IsSilhouette(inventory, standIn) ? standIn : null;
    }

    /// <summary>
    /// The object takes its slot back from its silhouette stand-in (vanilla's own shape), before
    /// vanilla acts on it by identity. False when it has no silhouette here.
    /// </summary>
    public static bool ToObject(DV.InventorySystem.Inventory inventory, GameObject item)
    {
        InventoryStandIn standIn = Of(inventory, item);
        if (standIn == null || VanillaInventorySlots.Holds(inventory, item))
            return false;
        VanillaInventorySlots.Rebind(inventory, standIn.gameObject, item);
        standIn.Retire();
        return true;
    }

    /// <summary>
    /// An object vanilla keeps as a dropped entry, out of the local player's hands, gives its slot
    /// to its stand-in: the silhouette. Only while this game's inventory is stand-ins, and not on
    /// an enabled VR belt slot (the belt keeps objects, OI-COLD-14) or for job papers (C7).
    /// </summary>
    public static void ToStandIn(DV.InventorySystem.Inventory inventory, GameObject item)
    {
        if (inventory == null || item == null || InventoryRowHolder.Current == null ||
            item.GetComponent<InventoryStandIn>() != null || !item.TryGetComponent(out NetworkedItem networked) ||
            networked.NetId == 0 || networked.Item?.InventorySpecs == null)
            return;
        string prefabName = networked.Item.InventorySpecs.itemPrefabName;
        int slot = VanillaInventorySlots.SlotOf(inventory, item);
        if (slot < 0 || !inventory.InventoryItems[slot].IsDropped || inventory.GetEquipSlotForItem(item) >= 0 ||
            inventory.IsValidVRBeltIndex(slot) || !InventoryStandIn.CanStandIn(prefabName))
            return;
        if (InventoryStandIn.TryGet(networked.NetId, out InventoryStandIn other) &&
            VanillaInventorySlots.Holds(inventory, other.gameObject))
        {
            Multiplayer.LogWarning($"Inventory: {prefabName} (netId {networked.NetId}) has a stand-in in slot " +
                                   $"{VanillaInventorySlots.SlotOf(inventory, other.gameObject)}; its silhouette in slot {slot} stays its object");
            return;
        }
        InventoryStandIn standIn = InventoryStandIn.For(networked.NetId, prefabName);
        standIn.Relation = networked.Relation;
        standIn.ClaimSlot = networked.InventoryClaimSlot;
        standIn.ClaimFlags = networked.InventoryClaimFlags;
        standIn.Revision = networked.AuthorityRevision;
        VanillaInventorySlots.Rebind(inventory, item, standIn.gameObject);
        NetworkedItemManager.TraceItem("item.silhouette-as-stand-in", networked, new() { ["slot"] = slot });
    }

    // Vanilla's lookups by the object find its silhouette.
    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.IndexOf))]
    [HarmonyPostfix]
    private static void IndexOf(DV.InventorySystem.Inventory __instance, GameObject item, ref int __result)
    {
        if (__result < 0 && Of(__instance, item) is { } standIn)
            __result = VanillaInventorySlots.SlotOf(__instance, standIn.gameObject);
    }

    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.Contains))]
    [HarmonyPostfix]
    private static void Contains(DV.InventorySystem.Inventory __instance, GameObject item, bool includeDropped, ref bool __result)
    {
        if (!__result && includeDropped && Of(__instance, item) != null)
            __result = true;
    }

    // Vanilla acting on the object by identity: it's in its slot first.
    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.EquipItem), new[] { typeof(GameObject), typeof(int), typeof(int) })]
    [HarmonyPrefix]
    private static void BeforeEquip(DV.InventorySystem.Inventory __instance, GameObject item)
    {
        equipping++;
        ToObject(__instance, item);
    }

    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.EquipItem), new[] { typeof(GameObject), typeof(int), typeof(int) })]
    [HarmonyFinalizer]
    private static void AfterEquip() => equipping--;

    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.AddItemToInventory), new[] { typeof(GameObject), typeof(bool) })]
    [HarmonyPrefix]
    private static void BeforeAdd(DV.InventorySystem.Inventory __instance, GameObject item) => ToObject(__instance, item);

    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.AddItemToInventory), new[] { typeof(GameObject), typeof(int), typeof(bool) })]
    [HarmonyPrefix]
    private static void BeforeAddAt(DV.InventorySystem.Inventory __instance, GameObject item) => ToObject(__instance, item);

    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.PurgeFromInventory))]
    [HarmonyPrefix]
    private static void BeforePurge(DV.InventorySystem.Inventory __instance, GameObject item) => ToObject(__instance, item);

    // Vanilla leaving the object dropped in its slot, out of the hands: the stand-in takes it.
    [HarmonyPatch("UnequipItem", new[] { typeof(bool), typeof(int), typeof(bool), typeof(InventoryActionType), typeof(InventorySlotState) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
    [HarmonyPrefix]
    private static void BeforeUnequip(DV.InventorySystem.Inventory __instance, int equippedSlot, out GameObject __state) =>
        __state = __instance.GetEquippedItemAtSlot(equippedSlot);

    [HarmonyPatch("UnequipItem", new[] { typeof(bool), typeof(int), typeof(bool), typeof(InventoryActionType), typeof(InventorySlotState) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
    [HarmonyPostfix]
    private static void AfterUnequip(DV.InventorySystem.Inventory __instance, bool addToInventory, GameObject __state)
    {
        // An equip moving the item from one hand to the other keeps it in a hand.
        if (!addToInventory && equipping == 0)
            ToStandIn(__instance, __state);
    }

    [HarmonyPatch(nameof(DV.InventorySystem.Inventory.DropItemFromHandsOrInventory), new[] { typeof(int), typeof(bool) })]
    [HarmonyPostfix]
    private static void AfterDrop(DV.InventorySystem.Inventory __instance, GameObject __result) => ToStandIn(__instance, __result);

    [HarmonyPatch("AddItemToInventory", new[] { typeof(GameObject), typeof(int), typeof(bool), typeof(bool) })]
    [HarmonyPostfix]
    private static void AfterAddAsDropped(DV.InventorySystem.Inventory __instance, GameObject item, bool addAsDropped)
    {
        if (addAsDropped)
            ToStandIn(__instance, item);
    }
}
