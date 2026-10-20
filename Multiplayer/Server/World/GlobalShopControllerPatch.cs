using DV.Shops;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.World;

/// <summary>
/// The host owns the shops' stock: a restock goes to the players. Purchases send the stock
/// themselves (<c>Server/Shops/ServerShops.Buy</c>); a client's own side is
/// <c>Client/World/GlobalShopControllerClientPatch</c>.
/// </summary>
[HarmonyPatch(typeof(GlobalShopController))]
public static class GlobalShopControllerPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(GlobalShopController.Restock))]
    private static void RestockPostfix()
    {
        if (NetworkLifecycle.Instance?.IsHost() == true)
            NetworkServer.Instance?.SendShopStock();
    }
}
