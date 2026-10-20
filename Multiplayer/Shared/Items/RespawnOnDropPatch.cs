using HarmonyLib;
using DV;
using DV.CabControls;
using DV.Items;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Shared.Items;

/// <summary>
/// Observes when Derail Valley schedules its distance-based respawn/destruction coroutine, and
/// suppresses it for items Multiplayer manages (the host's Lost and Found scanner owns those).
/// Unmanaged scene objects keep vanilla behaviour.
/// </summary>
[HarmonyPatch(typeof(RespawnOnDrop), nameof(RespawnOnDrop.RespawnOrDestroy))]
internal static class RespawnOnDropPatch
{
    [HarmonyPrefix]
    private static bool BeforeRespawnOrDestroy(RespawnOnDrop __instance, float delay, ref System.Collections.IEnumerator __result)
    {
        if (__instance == null)
            return true;
        // RespawnOrDestroy is a coroutine factory. Skipping it must still hand back a valid
        // enumerator: vanilla passes the result straight to CoroutineManager.Run, and a null
        // throws inside RespawnOnDrop.Checker, killing that item's distance/kinematic checker.
        __result = System.Linq.Enumerable.Empty<object>().GetEnumerator();
        GameObject itemObject = __instance.gameObject;
        ItemBase item = itemObject.GetComponent<ItemBase>();
        NetworkedItem.TryGetNetworkedItem(item, out NetworkedItem networked);
        Rigidbody rigidbody = item?.ItemRigidbody;
        if (DebugRuntime.EnabledFor("inventory"))
            DebugRuntime.Publish("inventory", "item.respawn-or-destroy-scheduled",
                NetworkRole.DebugSide,
                DebugSeverity.Warning, "Item", networked != null && networked.NetId != 0 ? networked.NetId.ToString() : $"unity:{itemObject.GetInstanceID()}",
                new Dictionary<string, object>
                {
                    ["delaySeconds"] = delay,
                    ["belongsToPlayer"] = item?.InventorySpecs?.BelongsToPlayer ?? false,
                    ["isEssential"] = item?.InventorySpecs?.IsEssential ?? false,
                    ["respawnOnDropThroughFloor"] = __instance.respawnOnDropThroughFloor,
                    ["onValidRespawnParent"] = __instance.OnValidRespawnParent,
                    ["position"] = DebugValueSnapshotter.Snapshot(itemObject.transform.position),
                    ["velocity"] = DebugValueSnapshotter.Snapshot(rigidbody?.velocity),
                    ["activeInHierarchy"] = itemObject.activeInHierarchy,
                    ["suppressedByMultiplayer"] = MultiplayerStoragePatchGuard.IsManaged(item, out _)
                });

        // The host scanner owns distance collection for canonical player items. Clients
        // must never run a process-local distance decision. Preserve vanilla reset/destroy
        // behavior for unowned scene objects and other non-network-managed items.
        return !MultiplayerStoragePatchGuard.IsManaged(item, out _);
    }
}
