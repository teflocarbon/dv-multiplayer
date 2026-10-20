using DV.CashRegister;
using DV.Shops;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Shops;

/// <summary>
/// The shops' registers and shelf items by index, as the wire names them: a shop is its index in
/// <c>GlobalShopController.globalShopList</c>, a shelf item its index in the shop's
/// <c>scanItemResourceModules</c> (the same on every game: the join checks the shops match).
/// </summary>
public static class ShopRegisters
{
    /// <summary>True in a multiplayer session: shop registers belong to the server then.</summary>
    public static bool ServerOwned => NetworkLifecycle.Instance != null &&
                                      (NetworkLifecycle.Instance.IsServerRunning || NetworkLifecycle.Instance.LocalSession != null);

    public static bool TryGetShopIndex(CashRegisterBase register, out int shopIndex)
    {
        shopIndex = -1;
        GlobalShopController shops = GlobalShopController.Instance;
        if (register == null || shops == null)
            return false;
        for (int i = 0; i < shops.globalShopList.Count; i++)
        {
            if (shops.globalShopList[i] != null && shops.globalShopList[i].cashRegister == register)
            {
                shopIndex = i;
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether <paramref name="register"/> is a shop's and a session owns it.</summary>
    public static bool IsServerOwnedShop(CashRegisterBase register) =>
        ServerOwned && TryGetShopIndex(register, out _);

    public static bool TryGetShop(int shopIndex, out Shop shop)
    {
        GlobalShopController shops = GlobalShopController.Instance;
        shop = shops != null && shopIndex >= 0 && shopIndex < shops.globalShopList.Count ? shops.globalShopList[shopIndex] : null;
        return shop != null;
    }

    public static bool TryGetModule(int shopIndex, int moduleIndex, out ScanItemCashRegisterModule module)
    {
        module = null;
        if (!TryGetShop(shopIndex, out Shop shop) || shop.scanItemResourceModules == null ||
            moduleIndex < 0 || moduleIndex >= shop.scanItemResourceModules.Length)
            return false;
        module = shop.scanItemResourceModules[moduleIndex];
        return module != null;
    }

    /// <summary>Finds a shelf item's shop and its index there.</summary>
    public static bool TryLocate(ScanItemCashRegisterModule module, out int shopIndex, out int moduleIndex)
    {
        shopIndex = moduleIndex = -1;
        GlobalShopController shops = GlobalShopController.Instance;
        if (module == null || shops == null)
            return false;
        for (int s = 0; s < shops.globalShopList.Count; s++)
        {
            ScanItemCashRegisterModule[] modules = shops.globalShopList[s]?.scanItemResourceModules;
            if (modules == null)
                continue;
            int m = System.Array.IndexOf(modules, module);
            if (m >= 0)
            {
                shopIndex = s;
                moduleIndex = m;
                return true;
            }
        }
        return false;
    }
}
