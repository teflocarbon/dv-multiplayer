using DV.Logic.Job;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;

namespace Multiplayer.Patches.Jobs;

[HarmonyPatch(typeof(Station), nameof(Station.AddJobToStation))]
public static class Station_AddJobToStation_Patch
{
    private static bool Prefix(Station __instance, Job job)
    {
        Multiplayer.Log($"Station.AddJobToStation() adding NetworkJob for stationId: {__instance.ID}, jobId: {job.ID}");

        if (NetworkLifecycle.Instance.IsHost())
            NetworkedStationController.RegisterOrQueueHostJob(__instance, job);

        // Never suppress the base game's station insertion or document spawn. If the
        // network station is still initializing, registration is completed later.
        return true;
    }
}
