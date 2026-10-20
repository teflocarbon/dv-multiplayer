using System.Collections.Generic;
using System.Linq;
using DV.Shops;
using Multiplayer.Core.Shops;

namespace Multiplayer.Shared.Shops;

/// <summary>
/// This game's shops as the network sees them: the stock table's items and each shop's items, in
/// the order the indices on the wire refer to. The host refuses a client whose shops differ (see
/// <see cref="ShopCatalogueFingerprint"/>).
/// </summary>
public static class ShopCatalogue
{
    /// <summary>Default when the world has no shops yet.</summary>
    public static ShopCatalogueFingerprint Compute()
    {
        GlobalShopController shops = GlobalShopController.Instance;
        if (shops == null)
            return default;

        List<string> items = shops.shopItemsData.Select(d => d?.item?.ItemPrefabName).ToList();
        List<IReadOnlyList<string>> perShop = shops.globalShopList
            .Select(shop => (IReadOnlyList<string>)(shop?.scanItemResourceModules ?? new ScanItemCashRegisterModule[0])
                .Select(module => module?.sellingItemSpec?.ItemPrefabName)
                .ToList())
            .ToList();
        return ShopCatalogueFingerprint.Compute(items, perShop);
    }
}
