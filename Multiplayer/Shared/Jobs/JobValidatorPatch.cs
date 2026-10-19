using DV.ThingTypes;
using HarmonyLib;

namespace Multiplayer.Shared.Jobs;

/// <summary>
/// Every game: each station finds its validator. The server's and a remote client's halves of
/// taking and handing in a job are Server/Jobs/JobValidatorServerPatch and
/// Client/Jobs/JobValidatorClientPatch; both start with the checks here.
/// </summary>
[HarmonyPatch(typeof(JobValidator))]
public static class JobValidator_Patch
{
    [HarmonyPatch(nameof(JobValidator.Start))]
    [HarmonyPostfix]
    private static void Start(JobValidator __instance)
    {
        NetworkedStationController.QueueJobValidator(__instance);
    }

    /// <summary>
    /// Whether this validator may take the overview now: not on cooldown, and the overview's job is
    /// networked and still available here. Plays the error sound if not, and destroys an overview
    /// whose job isn't.
    /// </summary>
    public static bool TryGetOverviewJob(JobValidator validator, JobOverview jobOverview, out NetworkedJob networkedJob)
    {
        networkedJob = null;
        if (validator.bookletPrinter.IsOnCooldown)
        {
            validator.bookletPrinter.PlayErrorSound();
            return false;
        }

        if (!NetworkedJob.TryGetFromJob(jobOverview.job, out networkedJob) || jobOverview.job.State != JobState.Available)
        {
            Multiplayer.LogWarning($"Processing JobOverview {jobOverview?.job?.ID} {(networkedJob == null ? "NetworkedJob not found!, " : "")}Job state: {jobOverview?.job?.State}");
            validator.bookletPrinter.PlayErrorSound();
            jobOverview.DestroyJobOverview();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether this validator may take the booklet now: not on cooldown, and the booklet's job is
    /// networked and in progress here. Plays the error sound if not, and destroys a booklet whose
    /// job isn't.
    /// </summary>
    public static bool TryGetBookletJob(JobValidator validator, JobBooklet jobBooklet, out NetworkedJob networkedJob)
    {
        networkedJob = null;
        if (validator.bookletPrinter.IsOnCooldown)
        {
            validator.bookletPrinter.PlayErrorSound();
            return false;
        }

        if (!NetworkedJob.TryGetFromJob(jobBooklet.job, out networkedJob) || jobBooklet.job.State != JobState.InProgress)
        {
            Multiplayer.LogWarning($"Validating Job {jobBooklet?.job?.ID} {(networkedJob == null ? "NetworkedJob not found!, " : "")}Job state: {jobBooklet?.job?.State}");
            validator.bookletPrinter.PlayErrorSound();
            jobBooklet.DestroyJobBooklet();
            return false;
        }

        return true;
    }
}
