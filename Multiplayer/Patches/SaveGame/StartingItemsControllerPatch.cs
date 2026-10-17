using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.SaveGame;

namespace Multiplayer.Patches.SaveGame;

/// <summary>
/// On a joining client, counts the items the host says the player already owns as present when
/// DV's starting-item and licence safeguards check what's missing. That list (combined from every
/// storage) is used only by those two safeguards, so DV then adds only the starting items and
/// licence papers the player really lacks, instead of a new set on every join that duplicated the
/// ones the host hands back. See OwnedItemsSummary.
/// </summary>
[HarmonyPatch(typeof(StartingItemsController), nameof(StartingItemsController.CombineAndResolveIllegalDuplicates))]
public static class StartingItemsControllerPatch
{
    private static void Postfix(List<StorageItemData> __result)
    {
        List<StorageItemData> owned = StartGameData_ServerSave.OwnedItems;
        if (owned == null || __result == null || NetworkLifecycle.Instance.IsHost())
            return;
        __result.AddRange(owned);
        Multiplayer.Log($"Starting items: {owned.Count} item(s) this player owns on the host count as present");
    }
}
