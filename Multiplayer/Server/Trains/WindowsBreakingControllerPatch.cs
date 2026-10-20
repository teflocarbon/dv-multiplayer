using DV.Damage;
using HarmonyLib;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using UnityEngine;
using Multiplayer.Server.Session;

namespace Multiplayer.Server.Trains;

[HarmonyPatch(typeof(WindowsBreakingController))]
public static class WindowsBreakingController_BreakWindowsFromCollision_Patch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(WindowsBreakingController.BreakWindowsFromCollision))]
    public static void BreakWindowsFromCollision_Postfix(WindowsBreakingController __instance, Vector3 forceDirection)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        TrainCar car = TrainCar.Resolve(__instance.transform);
        if (car == null)
        {
            Multiplayer.LogWarning($"BreakWindowsFromCollision failed, unable to resolve TrainCar");
            return;
        }

        ushort netId = car.GetNetId();
        if(netId == 0)
        {
            Multiplayer.LogWarning($"BreakWindowsFromCollision failed, {car.name}");
            return; 
        }

        NetworkServer.Instance.SendWindowsBroken(netId, forceDirection);
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(WindowsBreakingController.RepairWindows))]
    public static void RepairWindows_Postfix(WindowsBreakingController __instance)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        TrainCar car = TrainCar.Resolve(__instance.transform);
        ushort netId = car.GetNetId();

        if (car == null ||netId == 0)
        {
            Multiplayer.LogWarning($"RepairWindows_Postfix failed, {car?.name}");
            return;
        }

        Multiplayer.LogDebug(()=>$"RepairWindows_Postfix, {car.name}");
        NetworkServer.Instance.SendWindowsRepaired(netId);
    }
}
