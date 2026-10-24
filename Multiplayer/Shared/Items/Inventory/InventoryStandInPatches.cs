using DV.Common;
using DV.InventorySystem;
using DV.Items;
using DV.UI.Inventory;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Shared.Items.Inventory;

/// <summary>
/// Vanilla's inventory with a stand-in in a slot (docs/design/items/inventory.md, "Stand-ins"):
/// every hook <c>InventoryDV</c> answers through the slot object's <c>ItemBase</c> is answered from
/// the stand-in's row and prefab, nothing parks, unparks or lays out a stand-in as a world object,
/// and the listeners of vanilla's events that need an item's own components skip it. Nothing here
/// changes anything for a real item.
/// </summary>
[HarmonyPatch]
internal static class InventoryStandInPatches
{
    private static InventoryStandIn StandIn(GameObject item) => item != null ? item.GetComponent<InventoryStandIn>() : null;

    [HarmonyPatch(typeof(InventoryDV), "IsEssential")]
    [HarmonyPrefix]
    private static bool IsEssential(GameObject item, ref bool __result)
    {
        if (StandIn(item) is not { } standIn)
            return true;
        __result = standIn.IsEssentialForInventory;
        return false;
    }

    [HarmonyPatch(typeof(InventoryDV), "IsGrabbed")]
    [HarmonyPrefix]
    private static bool IsGrabbed(GameObject item, ref bool __result)
    {
        if (StandIn(item) == null)
            return true;
        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(InventoryDV), "IsPlaceableInBelt")]
    [HarmonyPrefix]
    private static bool IsPlaceableInBelt(GameObject item, ref bool __result)
    {
        if (StandIn(item) is not { } standIn)
            return true;
        __result = standIn.IsBeltSnappable;
        return false;
    }

    [HarmonyPatch(typeof(InventoryDV), "IsValidItem")]
    [HarmonyPrefix]
    private static bool IsValidItem(GameObject item, ref bool __result)
    {
        if (StandIn(item) == null)
            return true;
        __result = true;
        return false;
    }

    [HarmonyPatch(typeof(InventoryDV), "IsTwoHanded")]
    [HarmonyPrefix]
    private static bool IsTwoHanded(GameObject item, ref bool __result)
    {
        if (StandIn(item) is not { } standIn)
            return true;
        __result = standIn.IsTwoHandedItem;
        return false;
    }

    // Nothing to end, lay out or park: a stand-in is never in the world.
    [HarmonyPatch(typeof(InventoryDV), "ForceEndInteraction")]
    [HarmonyPrefix]
    private static bool ForceEndInteraction(GameObject item) => StandIn(item) == null;

    [HarmonyPatch(typeof(InventoryDV), "AssignInventoryLayer")]
    [HarmonyPrefix]
    private static bool AssignInventoryLayer(GameObject item) => StandIn(item) == null;

    [HarmonyPatch(typeof(InventoryDV), "AssignWorldItemLayer")]
    [HarmonyPrefix]
    private static bool AssignWorldItemLayer(GameObject item) => StandIn(item) == null;

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), "RemoveItemFromWorld")]
    [HarmonyPrefix]
    private static bool RemoveItemFromWorld(GameObject item) => StandIn(item) == null;

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), "FinalizeRemoveItemFromWorld")]
    [HarmonyPrefix]
    private static bool FinalizeRemoveItemFromWorld(GameObject item) => StandIn(item) == null;

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), nameof(DV.InventorySystem.Inventory.ReturnItemToWorld))]
    [HarmonyPrefix]
    private static bool ReturnItemToWorld(GameObject item) => StandIn(item) == null;

    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), nameof(DV.InventorySystem.Inventory.DestroyItem))]
    [HarmonyPrefix]
    private static bool DestroyItem(GameObject item) => StandIn(item) == null;

    // Fires the item's own inventory events (ItemBase.FireInventoryStateChanged): a stand-in has none.
    [HarmonyPatch(typeof(InventoryItemEventsPropagator), "PropagateToItem")]
    [HarmonyPrefix]
    private static bool PropagateToItem(GameObject item) => StandIn(item) == null;

    // Holds or lets go of the equipped object in the hand: the hand never holds a stand-in.
    [HarmonyPatch(typeof(GrabberStashingHandler), "HandleEquipChange")]
    [HarmonyPrefix]
    private static bool HandleEquipChange(GameObject item) => StandIn(item) == null;

    [HarmonyPatch(typeof(GrabberStashingHandler), "SetDroppedItemTransformValues")]
    [HarmonyPrefix]
    private static bool NotForStandInTransforms(Transform itemTransform) =>
        itemTransform == null || itemTransform.GetComponent<InventoryStandIn>() == null;

    // VR: the hands grab the slot's object as it's equipped and let go of it as it's stashed. A
    // stand-in is neither; the belt's own bookkeeping still runs.
    [HarmonyPatch(typeof(InventoryViewVR), "OnInventoryStatusChanged")]
    [HarmonyPrefix]
    private static bool VrViewSkipsStandIns(InventoryViewVR __instance, InventorySlotState originState,
        InventoryActionType originActionType, InventorySlotState targetState, InventoryActionType targetActionType)
    {
        if (StandIn(originState.item) == null && StandIn(targetState.item) == null)
            return true;
        try
        {
            __instance.beltVR?.OnInventoryStatusChanged(originState, originActionType, targetState, targetActionType);
        }
        catch (System.Exception exception)
        {
            Multiplayer.LogDebug(() => $"Inventory: the VR belt skipped a stand-in's event: {exception.Message}");
        }
        return false;
    }

    // The hotbar's name casts to the concrete spec; the belt check reads the slot object's ItemBase.
    [HarmonyPatch(typeof(InventoryHotbarProvider), nameof(InventoryHotbarProvider.GetLocalizedNameForItem))]
    [HarmonyPrefix]
    private static bool HotbarName(IInventoryItemSpec item, ref string __result)
    {
        if (item is not InventoryStandIn standIn)
            return true;
        __result = standIn.LocalizedName;
        return false;
    }

    [HarmonyPatch(typeof(InventoryProvider), nameof(InventoryProvider.IsBeltSnappable))]
    [HarmonyPrefix]
    private static bool BeltSnappable(IInventoryItemSpec spec, ref bool __result)
    {
        if (spec is not InventoryStandIn standIn)
            return true;
        __result = standIn.IsBeltSnappable;
        return false;
    }

    // Vanilla's inventory save lists the slots' ItemBases and logs an error for each slot without
    // one. A stand-in's row is saved by the mod, and vanilla's list is the mod's mirror (D2), so
    // while it saves, its copy of the slots has no stand-ins.
    private static bool savingStorage;

    [HarmonyPatch(typeof(StorageSerializer), nameof(StorageSerializer.SaveStorage))]
    [HarmonyPrefix]
    private static void BeforeSaveStorage() => savingStorage = true;

    [HarmonyPatch(typeof(StorageSerializer), nameof(StorageSerializer.SaveStorage))]
    [HarmonyFinalizer]
    private static void AfterSaveStorage() => savingStorage = false;

    // A copy (vanilla's cache, refilled on each call), so blanking it changes no slot.
    [HarmonyPatch(typeof(DV.InventorySystem.Inventory), nameof(DV.InventorySystem.Inventory.GetItemsArray))]
    [HarmonyPostfix]
    private static void SavedSlots(GameObject[] __result)
    {
        if (!savingStorage || __result == null)
            return;
        for (int i = 0; i < __result.Length; i++)
            if (StandIn(__result[i]) != null)
                __result[i] = null;
    }
}
