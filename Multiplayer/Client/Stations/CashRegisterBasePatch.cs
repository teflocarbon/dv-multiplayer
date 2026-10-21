using DV.CashRegister;
using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Shops;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Utils;
using System;

namespace Multiplayer.Client.Stations;

/// <summary>
/// Service-station registers (one with modules, not a shop's): paying in goes through the server.
/// Shop registers are the server's entirely and show its state (<c>Client/Shops/</c>), so these
/// leave them alone apart from turning vanilla's own cash handling off.
/// </summary>
[HarmonyPatch(typeof(CashRegisterBase))]
public class CashRegisterBasePatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CashRegisterBase.AddCash))]
    private static bool AddCash(CashRegisterBase __instance, double amount)
    {
        if (__instance is not CashRegisterWithModules cashRegisterWithModules || ShopRegisters.IsServerOwnedShop(__instance))
            return true;

        Multiplayer.LogDebug(() => $"AddCash() {__instance.GetObjectPath()}, Deposited: {amount}\r\n{Environment.StackTrace}");

        if (!NetworkedCashRegisterWithModules.TryGet(cashRegisterWithModules, out var netCashRegister))
        {
            Multiplayer.LogWarning($"Attempting to AddCash, but NetworkedCashRegisterWithModules not found for {cashRegisterWithModules.GetObjectPath()}");
            return true;
        }

        Inventory.Instance.AddMoney(amount);

        CoroutineManager.Instance.StartCoroutine(netCashRegister.AddCash(amount));

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CashRegisterBase.OnEnable))]
    private static bool OnEnable(CashRegisterBase __instance)
    {
        //Multiplayer.LogDebug(() => $"CashRegisterBase.OnEnable({__instance.GetObjectPath()}) {__instance.GetType()}");
        if (__instance is not CashRegisterWithModules)
            return true;

        // Vanilla's "walked away, cash back to the wallet" check: the server's for a shop register
        // (ServerShops), on every game, the host's included.
        if (ShopRegisters.IsServerOwnedShop(__instance))
            return false;

        return NetworkLifecycle.Instance.IsHost();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CashRegisterBase.OnDisable))]
    private static bool OnDisable(CashRegisterBase __instance)
    {
        //Multiplayer.LogDebug(() => $"CashRegisterBase.OnDisable({__instance.GetObjectPath()}) {__instance.GetType()}");
        if (__instance is not CashRegisterWithModules)
            return true;

        // Prevent clients from cancelling/returning cash on cash registers when loading the game or leaving the area
        __instance.StopAllCoroutines();
        return NetworkLifecycle.Instance.IsHost() && !ShopRegisters.IsServerOwnedShop(__instance);
    }
}
