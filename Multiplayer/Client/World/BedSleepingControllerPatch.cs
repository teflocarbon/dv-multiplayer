using DV;
using HarmonyLib;
using Multiplayer.Shared.Session;

namespace Multiplayer.Client.World;

[HarmonyPatch(typeof(BedSleepingController))]
public static class BedSleepingControllerPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(BedSleepingController.TurnOffAndEngageHandbrakeOnAllLocos))]
    private static bool TurnOffAndEngageHandbrakeOnAllLocos()
    {
        return CanRun();
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(BedSleepingController.EngageHandbrakesOnCurrentTrain))]
    private static bool EngageHandbrakesOnCurrentTrain()
    {
        return CanRun();
    }

    private static bool CanRun()
    {
        return NetworkLifecycle.Instance.IsHostPlayingAlone;
    }
}
