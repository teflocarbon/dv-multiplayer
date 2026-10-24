using DV.Items;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using UnityEngine;

namespace Multiplayer.Shared.Items.Behaviours;

/// <summary>
/// Ammo going into or out of a tool's magazine (docs/design/items/containers.md, "Ammo in a
/// magazine"). Vanilla makes the ammo inactive in its slot, so the item's own frame never sees it
/// go in; this tells it, and its game reports the new home. A load the server applies isn't reported
/// back (NetworkedItem.MarkLocalStateDirty).
/// </summary>
[HarmonyPatch(typeof(ItemMagazine))]
public static class MagazinePatch
{
    [HarmonyPatch(nameof(ItemMagazine.AddItem))]
    [HarmonyPostfix]
    static void AddItem(GameObject item, bool __result)
    {
        if (__result && item != null && item.TryGetComponent(out NetworkedItem networked))
            networked.NoteMagazineChanged("magazine-loaded");
    }

    [HarmonyPatch(nameof(ItemMagazine.RemoveItem), typeof(int), typeof(bool), typeof(bool))]
    [HarmonyPrefix]
    static void RemoveItemBefore(ItemMagazine __instance, int index, out GameObject __state) =>
        __state = index >= 0 && index < __instance.Capacity ? __instance[index] : null;

    [HarmonyPatch(nameof(ItemMagazine.RemoveItem), typeof(int), typeof(bool), typeof(bool))]
    [HarmonyPostfix]
    static void RemoveItem(bool __result, GameObject __state)
    {
        if (__result && __state != null && __state.TryGetComponent(out NetworkedItem networked))
            networked.NoteMagazineChanged("magazine-unloaded");
    }
}
