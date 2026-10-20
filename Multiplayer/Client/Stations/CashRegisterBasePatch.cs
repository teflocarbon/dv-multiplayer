using DV.CashRegister;
using DV.InventorySystem;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Stations;
using Multiplayer.Shared.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
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

[HarmonyPatch]
public class CashRegisterBaseReturnMoneyToPlayerCheckPatch
{
    const int TARGET_NOPS = 3;
    static readonly CodeInstruction targetMethod = CodeInstruction.Call(typeof(Vector3), "op_Subtraction", [typeof(Vector3), typeof(Vector3)], null);

    public static IEnumerable<MethodBase> TargetMethods()
    {
        //We're targeting an 'IEnumerable'; this gets compiled as a state machine with
        //a method per state.
        //Find all of the resultant states that are a 'MoveNext', these are the methods we need to patch.
        //Doing this dynamically reduces the chance a game update breaks the transpiler
        return typeof(CashRegisterBase)
            .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(t => t.Name.StartsWith("<ReturnMoneyToPlayerCheck>"))
            .SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(m => m.Name == "MoveNext");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int nopCtr = 0;
        bool foundEntry = false;

        List<CodeInstruction> newCode = [] ;

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && instruction.operand?.ToString() == targetMethod.operand?.ToString())
            {
                foundEntry = true;
                newCode.Add(CodeInstruction.Call(typeof(DvExtensions), nameof(DvExtensions.AnyPlayerSqrMag), [typeof(Vector3)], null)); //inject our method
            }
            else if (foundEntry && nopCtr < TARGET_NOPS)
            {
                nopCtr++;
                newCode.Add(new CodeInstruction(OpCodes.Nop));
            }
            else
            {
                newCode.Add(instruction);
            }
        }

        return newCode;
    }
}
