using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Components.Networking.World;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Multiplayer.Patches.World.Items;

/// <summary>
/// Items are added to (and destroyed from) the inventory without going through the equip/drop
/// paths InventoryItemStatePatch observes, and DV deactivates them as they go in, so their
/// LateUpdate polling never sees the change either. On the host that left the authority record
/// at the item's first snapshot: starting items and anything stashed stayed recorded as World,
/// were persisted as world items at DV's hidden inventory position (~5 km up), and on the next
/// load fell out of the sky into Lost and Found or through the terrain. Observe the item itself.
/// </summary>
[HarmonyPatch]
public static class InventoryMembershipPatch
{
    public static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(Inventory))
            .Where(method => method.Name is nameof(Inventory.AddItemToInventory) or nameof(Inventory.DestroyItem) &&
                             method.GetParameters().FirstOrDefault()?.ParameterType == typeof(GameObject));

    [HarmonyPostfix]
    private static void ObserveItem(GameObject item, MethodBase __originalMethod)
    {
        NetworkedItem networked = item != null ? item.GetComponent<NetworkedItem>() : null;
        if (networked != null)
            NetworkedItemManager.Instance?.QueueLocalStateObservation(networked, "inventory." + __originalMethod.Name);
    }
}
