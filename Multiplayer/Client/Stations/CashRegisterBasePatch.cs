using DV.CashRegister;
using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Utils;
using System;
using Multiplayer.Client.Session;

namespace Multiplayer.Client.Stations;

[HarmonyPatch(typeof(CashRegisterBase))]
public class CashRegisterBasePatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CashRegisterBase.AddCash))]
    private static bool AddCash(CashRegisterBase __instance, double amount)
    {
        if (__instance is not CashRegisterWithModules cashRegisterWithModules)
            return true;

        Multiplayer.LogDebug(() => $"AddCash() {__instance.GetObjectPath()}, Deposited: {amount}\r\n{Environment.StackTrace}");

        if (!NetworkedCashRegisterWithModules.TryGet(cashRegisterWithModules, out var netCashRegister))
        {
            Multiplayer.LogWarning($"Attempting to AddCash, but NetworkedCashRegisterWithModules not found for {cashRegisterWithModules.GetObjectPath()}");
            return true;
        }

        if (netCashRegister.IsShopRegister)
        {
            // Shops run on the client, but the money paid in came out of the client's copy of the
            // shared wallet (or a banknote the host has just credited to it): the host's balance
            // never paid, and its next update handed the money back - clients shopped for free.
            if (!NetworkLifecycle.Instance.IsHost() && !NetworkLifecycle.Instance.IsProcessingPacket &&
                amount > double.Epsilon)
                NetworkClient.Instance?.SendShopPayment(amount);
            return true;
        }

        Inventory.Instance.AddMoney(amount);

        CoroutineManager.Instance.StartCoroutine(netCashRegister.AddCash(amount));

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("ReturnLeftoverDepositedCash")]
    private static bool ReturnLeftoverDepositedCash(CashRegisterBase __instance)
    {
        if (__instance.DepositedCash <= double.Epsilon || NetworkLifecycle.Instance.IsHost() ||
            NetworkLifecycle.Instance.IsProcessingPacket)
            return true;

        if (__instance is not CashRegisterWithModules cashRegisterWithModules ||
            !NetworkedCashRegisterWithModules.TryGet(cashRegisterWithModules, out var netCashRegister) ||
            !netCashRegister.IsShopRegister)
            return true;

        // A printed note would only exist on this client, so spending it later would credit nothing.
        // Hand the change back through the host, which caps it at what this player paid in.
        NetworkClient.Instance?.SendShopPayment(-__instance.DepositedCash);
        __instance.SetCash(0);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CashRegisterBase.OnEnable))]
    private static bool OnEnable(CashRegisterBase __instance)
    {
        //Multiplayer.LogDebug(() => $"CashRegisterBase.OnEnable({__instance.GetObjectPath()}) {__instance.GetType()}");
        if (__instance is not CashRegisterWithModules)
            return true;

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
        return NetworkLifecycle.Instance.IsHost();
    }
}
