using DV.Shops;
using HarmonyLib;
using Multiplayer.Components.Networking;

namespace Multiplayer.Patches.World;

[HarmonyPatch(typeof(GlobalShopController))]
public static class GlobalShopControllerPatch
{
    // A client's purchase used to spawn the items on that client only: nobody else saw them and
    // the host never saved them. The host spawns them now and owns the stock.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GlobalShopController.AddItemToInstantiationQueue))]
    private static bool AddItemToInstantiationQueue(GlobalShopController __instance, InventoryItemSpec boughtItemSpec,
        Shop shop, int amountBought)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !lifecycle.IsClientRunning || lifecycle.IsHost() || boughtItemSpec == null)
            return true;

        lifecycle.Client?.SendShopPurchase(__instance.globalShopList.IndexOf(shop), boughtItemSpec.ItemPrefabName,
            amountBought);
        // Show the stock going down straight away; the host's stock table follows.
        ShopItemData data = __instance.GetShopItemData(boughtItemSpec);
        if (data != null)
            data.purchasedItems += amountBought;
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GlobalShopController.AddItemToInstantiationQueue))]
    private static void AddItemToInstantiationQueuePostfix()
    {
        if (NetworkLifecycle.Instance?.IsHost() == true)
            NetworkLifecycle.Instance.Server?.SendShopStock();
    }

    // Stock is the host's: a client destroying its copy of a bought item must not restock.
    [HarmonyPrefix]
    [HarmonyPatch(nameof(GlobalShopController.Restock))]
    private static bool Restock()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        return lifecycle == null || !lifecycle.IsClientRunning || lifecycle.IsHost();
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(GlobalShopController.Restock))]
    private static void RestockPostfix()
    {
        if (NetworkLifecycle.Instance?.IsHost() == true)
            NetworkLifecycle.Instance.Server?.SendShopStock();
    }
}
