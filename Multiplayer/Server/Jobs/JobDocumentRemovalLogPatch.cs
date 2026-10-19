using HarmonyLib;
using Multiplayer.Server.Items;

namespace Multiplayer.Server.Jobs;

/// <summary>
/// The server notes why the job system destroys a booklet or overview, while its callers are still
/// on the stack (<see cref="ItemChangeLog.NoteJobDocumentRemoval"/>).
/// </summary>
[HarmonyPatch]
public static class JobDocumentRemovalLogPatch
{
    [HarmonyPatch(typeof(JobBooklet), nameof(JobBooklet.DestroyJobBooklet))]
    [HarmonyPrefix]
    private static void DestroyJobBooklet(JobBooklet __instance)
    {
        if (__instance != null)
            ItemChangeLog.NoteJobDocumentRemoval(__instance, __instance.job, typeof(JobDocumentRemovalLogPatch));
    }

    [HarmonyPatch(typeof(JobOverview), nameof(JobOverview.DestroyJobOverview))]
    [HarmonyPrefix]
    private static void DestroyJobOverview(JobOverview __instance)
    {
        if (__instance != null)
            ItemChangeLog.NoteJobDocumentRemoval(__instance, __instance.job, typeof(JobDocumentRemovalLogPatch));
    }
}
