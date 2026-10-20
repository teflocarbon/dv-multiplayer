using DV.Shops;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Shops;

namespace Multiplayer.Client.World;

/// <summary>
/// A game's shops in a session: the server buys and spawns (<c>Server/Shops/ServerShops</c>) and
/// owns the stock (<c>Server/World/GlobalShopControllerPatch</c> sends it on).
/// </summary>
[HarmonyPatch(typeof(GlobalShopController))]
public static class GlobalShopControllerClientPatch
{
    // Purchases are the server's: a Buy is a request (Client/Shops/ShopRegisterPatches), so vanilla's
    // queue is never reached. If anything else gets here, spawning on this game alone would make
    // items nobody else has.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GlobalShopController.AddItemToInstantiationQueue))]
    private static bool AddItemToInstantiationQueue(InventoryItemSpec boughtItemSpec)
    {
        if (!ShopRegisters.ServerOwned)
            return true;
        Multiplayer.LogWarning($"Ignored a vanilla shop purchase of {boughtItemSpec?.ItemPrefabName}: purchases go through the server");
        return false;
    }

    // Stock is the host's: a client destroying its copy of a bought item must not restock.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GlobalShopController.Restock))]
    private static bool Restock()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        return lifecycle == null || !NetworkRole.IsRemoteClient;
    }
}
