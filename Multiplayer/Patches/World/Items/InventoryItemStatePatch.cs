using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Components.Networking.World;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Multiplayer.Patches.World.Items;

/// <summary>
/// Inventory transitions can deactivate an item before its MonoBehaviour receives another
/// update. Queue a detached observation after the base-game operation so repeated equip,
/// unequip and drop transitions use the same NetworkedItem state path as grab/release hooks.
/// </summary>
[HarmonyPatch]
public static class InventoryItemStatePatch
{
    private static readonly HashSet<string> ObservedMethods = new()
    {
        "EquipItem",
        "UnequipItem",
        "DropItemFromHandsOrInventory",
        "OnInventoryStatusChanged"
    };

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredMethods(typeof(Inventory))
            .Where(method => ObservedMethods.Contains(method.Name));
    }

    [HarmonyPostfix]
    private static void ObserveInventoryState(MethodBase __originalMethod)
    {
        NetworkedItemManager.Instance?.QueueInventoryStateObservations(
            "inventory." + (__originalMethod?.Name ?? "state-changed"));
    }
}
