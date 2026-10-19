using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Career;

/// <summary>
/// A player host's starting items: what its own player owns according to the save count as
/// present (see Shared/Career/StartingItemsControllerPatch).
/// </summary>
[HarmonyPatch(typeof(StartingItemsController))]
public static class StartingItemsServerPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(StartingItemsController.CombineAndResolveIllegalDuplicates))]
    private static void CombinePostfix(List<StorageItemData> __result)
    {
        if (__result == null || !NetworkLifecycle.Instance.IsHost())
            return;
        List<StorageItemData> owned = PerPlayerSaveFormat.LocalOwnedItems;
        if (owned == null)
            return;
        __result.AddRange(owned);
        Multiplayer.Log($"Starting items: {owned.Count} item(s) this player already owns count as present");
    }
}
