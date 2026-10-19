using HarmonyLib;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Jobs;

[HarmonyPatch(typeof(StationController))]
public static class StationController_Patch
{
    [HarmonyPatch(nameof(StationController.Awake))]
    [HarmonyPostfix]
    public static void Awake(StationController __instance)
    {
        __instance.gameObject.AddComponent<NetworkedStationController>();
    }

    [HarmonyPatch(nameof(StationController.ExpireAllAvailableJobsInStation))]
    [HarmonyPrefix]
    public static bool ExpireAllAvailableJobsInStation(StationController __instance)
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !lifecycle.IsServerRunning && !lifecycle.IsClientRunning)
            return true;
        return lifecycle.IsHost();
    }


}
