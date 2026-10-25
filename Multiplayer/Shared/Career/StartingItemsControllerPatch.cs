using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Career;

/// <summary>
/// While loading, DV adds any starting items (comms radio, maps, compass, wallet, ...) and licence
/// papers missing from the items it was given (StartingItemsSafeguard, LicensesSafeguard). In
/// multiplayer the server makes every player's starting items itself, owned and in their slots
/// (essentials.md, "The issuer": Server/Items/Essentials/StartingItemsIssuer), so neither runs in any
/// game: a client, a player host or a dedicated host. DV's inventory starts empty in each.
/// Licence papers aren't made in multiplayer at all (PhysicalLicensePaperPatch).
/// Nor does its duplicate pass (CombineAndResolveIllegalDuplicates, OI-VAN-01): it keeps one radio, one
/// wallet, ... in the whole save, which in multiplayer holds every player's, so it dropped all but
/// one player's from Lost and Found and the world at each load. Every list is kept whole.
/// </summary>
[HarmonyPatch(typeof(StartingItemsController))]
public static class StartingItemsControllerPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(StartingItemsController.StartingItemsSafeguard))]
    private static bool StartingItemsPrefix() => !NetworkLifecycle.Instance.IsSessionRunning;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(StartingItemsController.LicensesSafeguard))]
    private static bool LicensesPrefix() => !NetworkLifecycle.Instance.IsSessionRunning;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(StartingItemsController.CombineAndResolveIllegalDuplicates))]
    private static bool CombinePrefix(List<StorageItemData> itemsInventory, List<StorageItemData> itemsLostAndFound,
        List<StorageItemData> itemsWorld, List<StorageItemData> itemsInstalledGadgets, List<StorageItemData> itemsItemContainers,
        ref List<StorageItemData> __result)
    {
        if (!NetworkLifecycle.Instance.IsSessionRunning)
            return true;
        // Vanilla's own concatenation, with nothing removed.
        __result = itemsInventory.Concat(itemsLostAndFound).Concat(itemsWorld).Concat(itemsInstalledGadgets)
            .Concat(itemsItemContainers)
            .Where(t => t != null && !string.IsNullOrWhiteSpace(t.itemPrefabName))
            .ToList();
        return false;
    }
}
