using DV.CashRegister;
using HarmonyLib;
using Multiplayer.Shared.Protocol.Packets.Common;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Shops;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Utils;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.Stations;

[HarmonyPatch(typeof(CashRegisterWithModules))]
public class CashRegisterWithModulesPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CashRegisterWithModules.OnDisable))]
    private static bool OnDisable(CashRegisterWithModules __instance)
    {
        //Multiplayer.LogDebug(() => $"CashRegisterWithModules.OnDisable({__instance.GetObjectPath()})");
        if (__instance == null)
            return true;

        __instance?.StopAllCoroutines();
        __instance?.textController?.Clear();
        __instance?.SetupListeners(false);

        // Prevent clients from cancelling/returning cash on cash registers when loading the game or leaving the area
        // A shop register is the server's (Server/Shops/ServerShops): no game cancels it or returns its cash.
        // Quitting the game destroys the lifecycle before the registers.
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        return lifecycle != null && lifecycle.IsHost() && !ShopRegisters.IsServerOwnedShop(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CashRegisterWithModules.OnBuyPressed))]
    private static bool OnBuyPressed(CashRegisterWithModules __instance)
    {
        // A shop register's Buy is a request to the server (Client/Shops/ShopRegisterPatches).
        if (ShopRegisters.IsServerOwnedShop(__instance))
            return true;

        var player = PlayerManager.PlayerTransform.position;
        var reg = __instance.transform.position;
        var sqrMag = (player - reg).sqrMagnitude;
        Multiplayer.LogDebug(() => $"CashRegisterWithModules.OnBuyPressed() player pos: {player} register pos: {reg}, sqrMag: {sqrMag}");
        if (NetworkLifecycle.Instance.IsHost())
        {
            hostPressedBuy = true;
            hostBuyHappened = false;
            return true;
        }

        if (!NetworkedCashRegisterWithModules.TryGet(__instance, out var netCashRegister))
        {
            Multiplayer.LogWarning($"CashRegisterWithModules.OnBuyPressed({__instance.GetObjectPath()}) NetworkedCashRegisterWithModules not found!");
            return false;
        }

        CoroutineManager.Instance.StartCoroutine(netCashRegister.Buy());

        return false;
    }

    // The host's own press of Buy: vanilla refuses an empty basket or too little cash, and only a
    // buy that happened may go to the clients. Replaying a refused one emptied their register's
    // basket and deposited cash while the host kept both.
    private static bool hostPressedBuy;
    private static bool hostBuyHappened;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CashRegisterWithModules.Buy))]
    private static void Buy_Postfix(bool __result)
    {
        if (hostPressedBuy)
            hostBuyHappened = __result;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CashRegisterWithModules.OnBuyPressed))]
    private static void OnBuyPressed_Postfix(CashRegisterWithModules __instance)
    {
        if (ShopRegisters.IsServerOwnedShop(__instance))
            return;
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        bool happened = hostBuyHappened;
        hostPressedBuy = false;
        hostBuyHappened = false;
        if (!happened)
            return;

        if (!NetworkedCashRegisterWithModules.TryGet(__instance, out var netCashRegister))
        {
            Multiplayer.LogWarning($"CashRegisterWithModules.OnBuyPressed_Postfix({__instance.GetObjectPath()}) NetworkedCashRegisterWithModules not found!");
            return;
        }

        // Send buy action to all clients
        NetworkServer.Instance.SendCashRegisterAction(new CommonCashRegisterWithModulesActionPacket
        {
            NetId = netCashRegister.NetId,
            Action = CashRegisterAction.Buy,
            Amount = __instance.DepositedCash
        });
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(CashRegisterWithModules.Cancel))]
    private static bool Cancel(CashRegisterWithModules __instance)
    {
        if (ShopRegisters.IsServerOwnedShop(__instance))
            return true;

        //Multiplayer.LogDebug(()=>$"CashRegisterWithModules.Cancel({__instance.GetObjectPath()})\r\n{Environment.StackTrace}");

        if (NetworkLifecycle.Instance.IsHost())
            return true;

        if (!NetworkedCashRegisterWithModules.TryGet(__instance, out var netCashRegister))
        {
            Multiplayer.LogWarning($"CashRegisterWithModules.Cancel({__instance.GetObjectPath()}) NetworkedCashRegisterWithModules not found!");
            return false;
        }

        CoroutineManager.Instance.StartCoroutine(netCashRegister.Cancel());

        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CashRegisterWithModules.Cancel))]
    private static void Cancel_Postfix(CashRegisterWithModules __instance)
    {
        if (ShopRegisters.IsServerOwnedShop(__instance))
            return;
        //Multiplayer.LogWarning($"CashRegisterWithModules.Cancel_Postfix({__instance.GetObjectPath()})");
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedCashRegisterWithModules.TryGet(__instance, out var netCashRegister))
        {
            Multiplayer.LogWarning($"CashRegisterWithModules.Cancel_Postfix({__instance.GetObjectPath()}) NetworkedCashRegisterWithModules not found!");
            return;
        }

        // Send cancel action to all clients
        NetworkServer.Instance.SendCashRegisterAction(new CommonCashRegisterWithModulesActionPacket
        {
            NetId = netCashRegister.NetId,
            Action = CashRegisterAction.Cancel,
            Amount = __instance.DepositedCash
        });
    }
}
