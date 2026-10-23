using System.Collections.Generic;
using DV.ThingTypes;
using HarmonyLib;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using UnityEngine;

namespace Multiplayer.Client.Jobs;

/// <summary>
/// The station office bin (vanilla's <see cref="JobAbandoner"/>) abandons a taken job's booklet
/// and ignores overviews. Dropping an offered job's overview in it asks the server to re-roll the
/// job (Server/Jobs/JobReroll): every player asks, the host's own included (in-process). Only the
/// game whose player let go of the paper asks; everyone else's copy of it falls in too.
/// </summary>
[HarmonyPatch(typeof(JobAbandoner), nameof(JobAbandoner.OnTriggerEnter))]
public static class JobAbandoner_ClientPatch
{
    // The paper has several colliders and may bounce in again: one request per job this often.
    private const float RequestInterval = 2f;
    // How long after letting go the paper is still this player's, before the server's physics
    // lease for it arrives (a client) or if it never gets one.
    private const float ReleaseWindow = 5f;

    private static readonly Dictionary<ushort, float> lastRequestAt = [];

    private static void Postfix(JobAbandoner __instance, Collider other)
    {
        IJobsClient jobs = JobsClient.Current;
        if (jobs == null || other == null)
            return;
        JobOverview overview = other.GetComponentInParent<JobOverview>();
        if (overview == null || overview.job == null || overview.job.State != JobState.Available ||
            !overview.TryGetComponent(out NetworkedItem item) || item.NetId == 0 ||
            !NetworkedJob.TryGetFromJob(overview.job, out NetworkedJob job) || !IsLocalPlayers(item))
            return;

        float now = Time.realtimeSinceStartup;
        if (lastRequestAt.TryGetValue(job.NetId, out float last) && now - last < RequestInterval)
            return;
        lastRequestAt[job.NetId] = now;

        Multiplayer.Log($"Job overview {job.Job.ID} dropped in the bin; asking for a re-roll");
        jobs.SendJobRerollRequest(job.NetId, item.NetId,
            __instance.transform.position - WorldMover.currentMove);
    }

    // In this player's hand or inventory, simulated by this game, or let go of a moment ago.
    private static bool IsLocalPlayers(NetworkedItem item)
    {
        if (item.IsLocallyPossessed())
            return true;
        NetworkedItemManager items = NetworkedItemManager.Instance;
        if (items == null || items.IsSpatiallySimulatedRemotely(item.NetId))
            return false;
        return items.HasActiveSpatialState(item.NetId) ||
               item.LocallyReleasedAt >= 0f && Time.realtimeSinceStartup - item.LocallyReleasedAt < ReleaseWindow;
    }
}
