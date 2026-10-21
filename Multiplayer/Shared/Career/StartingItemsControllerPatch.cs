using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Career;

/// <summary>
/// While loading, DV adds any starting items (comms radio, maps, compass, wallet, ...) and licence
/// papers missing from the items it was given (StartingItemsSafeguard, LicensesSafeguard). In
/// multiplayer the server makes every player's starting items itself, owned and in their slots
/// (owned-items.md, PR 4: Server/Items/OwnedItems/StartingItemsIssuer), so neither runs in any
/// game: a client, a player host or a dedicated host. DV's inventory starts empty in each.
/// Licence papers aren't made in multiplayer at all (PhysicalLicensePaperPatch).
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
}
