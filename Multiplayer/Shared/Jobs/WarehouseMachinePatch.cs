using DV.Logic.Job;
using HarmonyLib;

namespace Multiplayer.Shared.Jobs;

[HarmonyPatch(typeof(WarehouseMachine))]
public class WarehouseMachinePatch
{
    [HarmonyPatch(nameof(WarehouseMachine.ID))]
    [HarmonyPatch(MethodType.Setter)]
    [HarmonyPostfix]
    public static void ID_Set(WarehouseMachine __instance)
    {
        WarehouseMachineLookup.Instance.RegisterWarehouseMachine( __instance );
    }
}
