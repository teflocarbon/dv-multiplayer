using HarmonyLib;
using DV;
using DV.CabControls;
using DV.Items;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Shared.Items.VanillaStorage;

/// <summary>
/// Derail Valley's distance-based respawn or destroy (OI-VAN-05; vanilla-storage.md, "Respawn"). In
/// multiplayer it never runs on a client for a networked item, and never puts one in vanilla's Lost and
/// Found list: the server decides (<see cref="IItemServer.AllowsRespawnOrDestroy"/>), letting only a
/// binned item's destroy and an unowned scene item's reset to its spawn run, as its own steps.
/// Objects with no NetId keep vanilla behaviour.
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

        if (!MultiplayerStoragePatchGuard.Active || networked == null || networked.NetId == 0)
            return true;
        // A client never decides; the server, for the few it allows.
        // A refusal leaves vanilla's checker latched (wentOutOfRange): the item isn't asked about again
        // until something clears it, which binning does (Binning.TryBin, K1).
        return ItemServer.Current?.AllowsRespawnOrDestroy(networked, __instance) == true;
    }

    // A destroy the server allowed carries its cause: a binned item's is its binner's (OI-VAN-02).
    [HarmonyPostfix]
    private static void AfterRespawnOrDestroy(RespawnOnDrop __instance, ref System.Collections.IEnumerator __result)
    {
        if (__instance == null || !MultiplayerStoragePatchGuard.Active || ItemServer.Current is not { } server ||
            !NetworkedItem.TryGetNetworkedItem(__instance.GetComponent<ItemBase>(), out NetworkedItem networked) ||
            networked == null || networked.NetId == 0)
            return;
        __result = Noted(__result, server, networked, __instance);
    }

    private static System.Collections.IEnumerator Noted(System.Collections.IEnumerator vanilla, IItemServer server, NetworkedItem item,
        RespawnOnDrop respawner)
    {
        bool first = true;
        while (true)
        {
            // Its first step only waits; the destroy is in a later one, within the frame of the note.
            // Only when vanilla's step will act: back in range of a player it stops instead (K6).
            if (!first && item != null && respawner != null &&
                respawner.CheckDistances() is (true, true, _, _))
                server.BeforeRespawnOrDestroyStep(item);
            first = false;
            if (!vanilla.MoveNext())
                yield break;
            yield return vanilla.Current;
        }
    }
}
