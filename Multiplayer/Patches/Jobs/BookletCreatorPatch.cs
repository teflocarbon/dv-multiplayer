using DV.Booklets;
using DV.Logic.Job;
using DV.ServicePenalty;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking.World;
using Multiplayer.Utils;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Networking.Data.Jobs;
using UnityEngine;


namespace Multiplayer.Patches.Jobs;

[HarmonyPatch(typeof(BookletCreator))]
public static class BookletCreator_Patch
{
    [HarmonyPatch(nameof(BookletCreator.CreateJobOverview))]
    [HarmonyPostfix]
    private static void CreateJobOverview(JobOverview __result, Job job)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedJob.TryGetFromJob(job, out NetworkedJob networkedJob))
        {
            Multiplayer.LogError($"BookletCreatorJob_Patch.CreateJobOverview() NetworkedJob not found for Job ID: {job.ID}");
        }
        else
        {
            NetworkedItem netItem = __result.GetOrAddComponent<NetworkedItem>();
            netItem.Initialize(__result, 0, false);
            networkedJob.JobOverview =  netItem;
        }
    }

    [HarmonyPatch(nameof(BookletCreator.CreateJobBooklet))]
    [HarmonyPostfix]
    private static void CreateJobBooklet(JobBooklet __result, Job job)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedJob.TryGetFromJob(job, out NetworkedJob networkedJob))
        {
            Multiplayer.LogError($"CreateJobBooklet() NetworkedJob not found for Job ID: {job.ID}");
        }
        else
        {
            NetworkedItem netItem = __result.GetOrAddComponent<NetworkedItem>();
            netItem.Initialize(__result, 0, false);
            networkedJob.RegisterJobBooklet(netItem, networkedJob.PendingBookletIssuedToPlayerId);
        }
    }

    [HarmonyPatch(nameof(BookletCreator.CreateJobReport))]
    [HarmonyPostfix]
    private static void CreateJobReport(JobReport __result, Job job, DisplayableDebt debt)
    {
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        if (!NetworkedJob.TryGetFromJob(job, out NetworkedJob networkedJob))
        {
            Multiplayer.LogError($"CreateJobReport() NetworkedJob not found for Job ID: {job.ID}");
        }
        else
        {
            NetworkedItem netItem = __result.GetOrAddComponent<NetworkedItem>();
            netItem.Initialize(__result, 0, false);
            networkedJob.AddReport(netItem);

            uint validationStationNetId = 0;
            if (NetworkedStationController.GetFromJobValidator(networkedJob.JobValidator, out NetworkedStationController station))
                validationStationNetId = station.NetId;

            JobReportArtifactData artifact = JobReportArtifactData.Capture(
                netItem.NetId,
                networkedJob.NetId,
                validationStationNetId,
                ItemPositionData.FromItem(netItem),
                job,
                debt);
            JobReportArtifactRegistry.Register(artifact);
        }
    }
}
