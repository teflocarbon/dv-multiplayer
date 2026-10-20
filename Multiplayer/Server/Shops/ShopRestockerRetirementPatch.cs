using DV.Shops;
using HarmonyLib;
using Multiplayer.Shared.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Shops;

/// <summary>
/// Vanilla restocks the shop whenever a bought item's GameObject is destroyed
/// (<c>ShopRestocker.OnDestroy</c>), because in single player that means the item is gone. The
/// server also destroys an item's GameObject when the item lives on somewhere else: deposited in a
/// cold container (it's a record in the container now), an aborted withdrawal's fresh copy, a
/// replaced scene item. Each of those restocked the shop, so putting a bought lighter in a toolbox
/// put it back on the shelf as well: unlimited items (docs/design/shops.md, gap 13). Those destroys
/// are marked as a cold-storage retirement first, and don't restock.
/// </summary>
[HarmonyPatch(typeof(ShopRestocker), "OnDestroy")]
internal static class ShopRestockerRetirementPatch
{
    [HarmonyPrefix]
    private static void OnDestroy(ShopRestocker __instance)
    {
        if (!__instance.restockOnItemDestroyed || NetworkLifecycle.Instance?.IsServerRunning != true)
            return;
        if (__instance.TryGetComponent(out NetworkedItem item) && item.ColdStorageRetirement)
            __instance.restockOnItemDestroyed = false;
    }
}
