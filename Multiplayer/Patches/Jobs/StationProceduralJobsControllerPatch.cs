using HarmonyLib;
using Multiplayer.Components.Networking;

namespace Multiplayer.Patches.Jobs;

[HarmonyPatch(typeof(StationProceduralJobsController), nameof(StationProceduralJobsController.TryToGenerateJobs))]
public static class StationProceduralJobsController_TryToGenerateJobs_Patch
{
    private static bool Prefix()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;
        if (lifecycle == null || !lifecycle.IsServerRunning && !lifecycle.IsClientRunning)
            return true;
        return lifecycle.IsHost();
    }
}
