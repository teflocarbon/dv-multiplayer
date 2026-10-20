using DV.Logic.Job;
using HarmonyLib;
using Multiplayer.Shared.Session;
using System.Reflection;

namespace Multiplayer.Server.Jobs;

/// <summary>
/// Loading a save, DV matches each taken job to its saved booklet, and when there is none it prints
/// a replacement straight into the local player's inventory (JobSaveManager.InitializeCorrespondingJobBooklet,
/// CreateAndInitializeJobBookletForJob). The mod saves no booklets (WorldItemPersistenceSave.IsJobSystemDocument),
/// so on every load each taken job gets one. A dedicated host's local player is a parked body, not
/// a player (as with StartingItemsControllerPatch): the server can't possess items, so the booklet
/// sat in an inventory nobody has while its record said World, and clients saw it lying at the
/// world origin. A dedicated host prints none; players reprint their booklet at a validator
/// (JobBookletSummonCoordinator), as a player who took the job on a player host does after a load.
/// </summary>
[HarmonyPatch]
public static class JobBookletRecoveryPatch
{
    // The coroutine's own MoveNext: the iterator method is a stub Mono inlines into its caller.
    private static MethodBase TargetMethod() =>
        AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(JobSaveManager), "CreateAndInitializeJobBookletForJob"));

    [HarmonyPrefix]
    private static bool SkipOnDedicatedHost(object __instance, ref bool __result)
    {
        if (!NetworkLifecycle.Instance.IsDedicatedHost)
            return true;
        string jobId = Traverse.Create(__instance).Field<Job>("job").Value?.ID;
        Multiplayer.Log($"Job {jobId}: no booklet printed into the dedicated host's inventory on load; players reprint it at a validator.");
        __result = false;
        return false;
    }
}
