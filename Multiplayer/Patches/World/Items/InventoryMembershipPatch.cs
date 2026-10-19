using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Components.Networking;
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

    // DV destroying an item from the inventory: its callers are on the stack only here.
    [HarmonyPrefix]
    private static void NoteDestroy(GameObject item, MethodBase __originalMethod)
    {
        if (__originalMethod.Name != nameof(Inventory.DestroyItem) || item == null ||
            NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsHost() ||
            !item.TryGetComponent(out NetworkedItem networked) || networked.NetId == 0)
            return;
        networked.NoteRemovalCause(ItemChangeLog.Removal(global::Multiplayer.Core.Items.ItemRemovalReason.DestroyedFromInventory,
            detail: "callers " + ItemChangeLog.Callers(typeof(InventoryMembershipPatch))));
    }

    [HarmonyPostfix]
    private static void ObserveItem(GameObject item, MethodBase __originalMethod)
    {
        // Host only: on a client the inventory is loaded and adopted through its own flow (local
        // copies are matched to the host's originals on join), and observing those adds as local
        // state changes raced it - a rejoining player's items were never returned.
        if (NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsHost())
            return;
        NetworkedItem networked = item != null ? item.GetComponent<NetworkedItem>() : null;
        if (networked != null)
            NetworkedItemManager.Instance?.QueueLocalStateObservation(networked, "inventory." + __originalMethod.Name);
    }
}
