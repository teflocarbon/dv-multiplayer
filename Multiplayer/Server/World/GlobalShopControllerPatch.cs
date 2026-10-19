using DV.Shops;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.World;

/// <summary>
/// The host owns the shops' stock: every change to it goes to the players. A client's own side is
/// <c>Client/World/GlobalShopControllerClientPatch</c>.
/// </summary>
[HarmonyPatch(typeof(GlobalShopController))]
public static class GlobalShopControllerPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(GlobalShopController.AddItemToInstantiationQueue))]
    private static void AddItemToInstantiationQueuePostfix()
    {
        if (NetworkLifecycle.Instance?.IsHost() == true)
            NetworkLifecycle.Instance.Server?.SendShopStock();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GlobalShopController.Restock))]
    private static void RestockPostfix()
    {
        if (NetworkLifecycle.Instance?.IsHost() == true)
            NetworkLifecycle.Instance.Server?.SendShopStock();
    }
}
