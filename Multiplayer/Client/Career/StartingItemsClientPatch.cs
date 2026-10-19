using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Client.Session;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.Career;

/// <summary>
/// A client's starting items: what the server says the player owns count as present (see
/// Shared/Career/StartingItemsControllerPatch).
/// </summary>
[HarmonyPatch(typeof(StartingItemsController))]
public static class StartingItemsClientPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(StartingItemsController.CombineAndResolveIllegalDuplicates))]
    private static void CombinePostfix(List<StorageItemData> __result)
    {
        if (__result == null || NetworkLifecycle.Instance.IsHost())
            return;
        List<StorageItemData> owned = StartGameData_ServerSave.OwnedItems;
        if (owned == null)
            return;
        __result.AddRange(owned);
        Multiplayer.Log($"Starting items: {owned.Count} item(s) this player already owns count as present");
    }
}
