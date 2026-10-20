using DV.CashRegister;
using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Shops;

namespace Multiplayer.Server.Shops;

/// <summary>
/// Vanilla saves the wallet plus the cash in every register as the player's money, so cash paid in
/// and not spent comes back when the save loads. A shop register's cash is the server's
/// (<see cref="ServerShops"/>): a dedicated host's vanilla registers show none of it, and a player
/// host's show the server's. So the saved money is the wallet, the service registers' cash, and the
/// server's tills (docs/design/shops.md, "Persistence").
/// </summary>
[HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.UpdateInternalData))]
public static class ShopTillSavePatch
{
    [HarmonyPostfix]
    private static void Postfix(SaveGameManager __instance)
    {
        NetworkServer server = NetworkServer.Instance;
        if (server == null || !ShopRegisters.ServerOwned)
            return;

        double money = Inventory.Instance.PlayerMoney + server.Shops.CashInTills();
        foreach (CashRegisterBase register in CashRegisterBase.allCashRegisters)
            if (!ShopRegisters.TryGetShopIndex(register, out _))
                money += register.DepositedCash;
        // As vanilla writes it.
        __instance.data.SetFloat(SaveGameKeys.Player_money, (float)money);
    }
}
