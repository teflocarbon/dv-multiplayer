using DV.Shops;
using HarmonyLib;
using Multiplayer.Core.Shops;
using UnityEngine;

namespace Multiplayer.Shared.Shops;

/// <summary>
/// Lays every shop's shelves out the same way on every game. Vanilla's <c>ShopShelves.Start</c>
/// seeds the layout from <c>UnityEngine.Random</c>, so each player saw the items in different
/// places. The server picks a session seed (<c>NetworkServer.OnLoaded</c>) and sends it at join;
/// each game picks the shop's verified seed from it (<see cref="ShopShelfSeed"/>) and lays the
/// shelves out again if they already were. See docs/design/shops.md.
/// </summary>
public static class ShopShelfLayout
{
    /// <summary>The session's seed; null outside a session, which leaves vanilla's random layout.</summary>
    public static int? SessionSeed { get; private set; }

    /// <summary>Uses <paramref name="seed"/> from now on, and lays out again the shelves already laid out.</summary>
    public static void Apply(int seed)
    {
        SessionSeed = seed;
        foreach (ShopShelves shelves in Object.FindObjectsOfType<ShopShelves>())
        {
            Shop shop = NearestShop(shelves);
            if (shop != null)
                shelves.SpawnShopItems(shop, default);
        }
    }

    public static void Reset() => SessionSeed = null;

    // The shop vanilla's Start lays out: the nearest one.
    private static Shop NearestShop(ShopShelves shelves)
    {
        GlobalShopController shops = GlobalShopController.Instance;
        if (shops == null)
            return null;

        Shop nearest = null;
        float best = float.MaxValue;
        foreach (Shop shop in shops.globalShopList)
        {
            if (shop == null)
                continue;
            float distance = Vector3.SqrMagnitude(shop.transform.position - shelves.transform.position);
            if (distance < best)
            {
                best = distance;
                nearest = shop;
            }
        }
        return nearest;
    }
}

[HarmonyPatch(typeof(ShopShelves), nameof(ShopShelves.SpawnShopItems))]
public static class ShopShelvesPatch
{
    // With a session seed, every game uses the same verified seed for this shop, on emptied shelves
    // (a second layout would otherwise find them full).
    [HarmonyPrefix]
    private static void SpawnShopItems(ShopShelves __instance, Shop shop, ref Unity.Mathematics.Random random)
    {
        if (ShopShelfLayout.SessionSeed is not int seed)
            return;
        int shopIndex = GlobalShopController.Instance?.globalShopList.IndexOf(shop) ?? -1;
        uint[] seeds = __instance.seeds?.seeds;
        if (shopIndex < 0 || seeds == null || seeds.Length == 0)
            return;

        foreach (ShelfPlacer placer in __instance.shelves)
            placer?.InitializeEmptyShelves();
        random = new Unity.Mathematics.Random(seeds[ShopShelfSeed.Pick(seed, shopIndex, seeds.Length)]);
    }
}
