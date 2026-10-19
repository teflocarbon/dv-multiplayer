using HarmonyLib;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Jobs;

/// <summary>
/// The server runs the vanilla validator: for the host's own player putting paperwork in it, and
/// for a client's request (NetworkServer.Jobs). A remote client's half is
/// Client/Jobs/JobValidatorClientPatch.
/// </summary>
[HarmonyPatch(typeof(JobValidator))]
public static class JobValidator_ServerPatch
{
    [HarmonyPatch(nameof(JobValidator.ProcessJobOverview))]
    [HarmonyPrefix]
    private static bool ProcessJobOverview(JobValidator __instance, JobOverview jobOverview)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return true;
        if (!JobValidator_Patch.TryGetOverviewJob(__instance, jobOverview, out NetworkedJob networkedJob))
            return false;

        Multiplayer.Log($"Processing JobOverview {jobOverview?.job?.ID}");
        networkedJob.JobValidator = __instance;
        // A client's request has set who the booklet is for; otherwise it's the host's own player.
        if (networkedJob.PendingBookletIssuedToPlayerId == 0)
            networkedJob.PendingBookletIssuedToPlayerId =
                NetworkLifecycle.Instance.Server.LocalPlayer?.PlayerId ?? 0;
        return true;
    }

    [HarmonyPatch(nameof(JobValidator.ProcessJobOverview))]
    [HarmonyPostfix]
    private static void ProcessJobOverviewComplete(JobOverview jobOverview)
    {
        if (NetworkLifecycle.Instance.IsHost() && jobOverview?.job != null &&
            NetworkedJob.TryGetFromJob(jobOverview.job, out NetworkedJob networkedJob))
            networkedJob.PendingBookletIssuedToPlayerId = 0;
    }

    [HarmonyPatch(nameof(JobValidator.ValidateJob))]
    [HarmonyPrefix]
    private static bool ValidateJob(JobValidator __instance, JobBooklet jobBooklet)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return true;
        if (!JobValidator_Patch.TryGetBookletJob(__instance, jobBooklet, out NetworkedJob networkedJob))
            return false;

        Multiplayer.Log($"Validating Job {jobBooklet?.job?.ID}");
        networkedJob.JobValidator = __instance;
        return true;
    }
}
