using DV.Shops;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.World;

/// <summary>
/// A client's shops: the host spawns what it buys and owns the stock
/// (<c>Server/World/GlobalShopControllerPatch</c> sends the stock on).
/// </summary>
[HarmonyPatch(typeof(GlobalShopController))]
public static class GlobalShopControllerClientPatch
{
    // A client's purchase used to spawn the items on that client only: nobody else saw them and
    // the host never saved them. The host spawns them now and owns the stock.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GlobalShopController.AddItemToInstantiationQueue))]
    private static bool AddItemToInstantiationQueue(GlobalShopController __instance, InventoryItemSpec boughtItemSpec,
        Shop shop, int amountBought)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !NetworkRole.IsRemoteClient || boughtItemSpec == null)
            return true;

        NetworkClient.Instance?.SendShopPurchase(__instance.globalShopList.IndexOf(shop), boughtItemSpec.ItemPrefabName,
            amountBought);
        // Show the stock going down straight away; the host's stock table follows.
        ShopItemData data = __instance.GetShopItemData(boughtItemSpec);
        if (data != null)
            data.purchasedItems += amountBought;
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
