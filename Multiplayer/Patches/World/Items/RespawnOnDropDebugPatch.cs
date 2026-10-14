using HarmonyLib;
using DV;
using DV.CabControls;
using DV.Items;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Patches.World.Items;

/// <summary>
/// Observes when Derail Valley schedules its distance-based respawn/destruction coroutine.
/// The eventual storage move is recorded by DebugInventoryObserver. No behavior is changed.
/// </summary>
[HarmonyPatch(typeof(RespawnOnDrop), nameof(RespawnOnDrop.RespawnOrDestroy))]
internal static class RespawnOnDropDebugPatch
{
    [HarmonyPrefix]
    private static bool BeforeRespawnOrDestroy(RespawnOnDrop __instance, float delay)
    {
        if (__instance == null)
            return true;
        GameObject itemObject = __instance.gameObject;
        ItemBase item = itemObject.GetComponent<ItemBase>();
        NetworkedItem.TryGetNetworkedItem(item, out NetworkedItem networked);
        Rigidbody rigidbody = item?.ItemRigidbody;
        if (DebugRuntime.EnabledFor("inventory"))
            DebugRuntime.Publish("inventory", "item.respawn-or-destroy-scheduled",
                NetworkLifecycle.Instance != null && NetworkLifecycle.Instance.IsHost() ? DebugRuntimeSide.Server : DebugRuntimeSide.Client,
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
