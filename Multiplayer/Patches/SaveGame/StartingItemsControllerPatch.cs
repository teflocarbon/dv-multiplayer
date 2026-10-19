using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.SaveGame;

namespace Multiplayer.Patches.SaveGame;

/// <summary>
/// While loading, DV adds any starting items (comms radio, maps, compass, wallet, ...) and licence
/// papers missing from the items it was given (StartingItemsSafeguard, LicensesSafeguard). In
/// multiplayer a player's items come back from the server, so DV only sees part of them; without
/// this it made a fresh set on every join, duplicating the ones the server hands back.
///
/// The list DV combines from every storage is used only by those two safeguards, so the items the
/// player already owns are added to it as present, and DV adds only what's really missing, as in
/// single player:
/// - a client: what the server says the player owns (OwnedItemsSummary, in the save-data packet);
/// - a player host: what its own player owns according to the save (PerPlayerSaveFormat).
/// A dedicated host's local player is a parked body, not a player, so it gets nothing.
/// </summary>
[HarmonyPatch(typeof(StartingItemsController))]
public static class StartingItemsControllerPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(StartingItemsController.CombineAndResolveIllegalDuplicates))]
    private static void CombinePostfix(List<StorageItemData> __result)
    {
        if (__result == null)
            return;
        List<StorageItemData> owned = NetworkLifecycle.Instance.IsHost()
            ? PerPlayerSaveFormat.LocalOwnedItems
            : StartGameData_ServerSave.OwnedItems;
        if (owned == null)
            return;
        __result.AddRange(owned);
        Multiplayer.Log($"Starting items: {owned.Count} item(s) this player already owns count as present");
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(StartingItemsController.StartingItemsSafeguard))]
    private static bool StartingItemsPrefix() => !NetworkLifecycle.Instance.IsDedicatedHost;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(StartingItemsController.LicensesSafeguard))]
    private static bool LicensesPrefix() => !NetworkLifecycle.Instance.IsDedicatedHost;
}
