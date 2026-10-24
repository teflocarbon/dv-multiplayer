using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.Booklets;
using DV.ThingTypes;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Debugging.Protocol;
using UnityEngine;

namespace Multiplayer.Server.Jobs;

public static class JobBookletSummonCoordinator
{
    private static bool running;

    public static bool TryStart(JobValidator validator, ServerPlayer requester)
    {
        if (validator == null || requester == null || !NetworkLifecycle.Instance.IsHost())
            return false;
        if (running || validator.bookletPrinter.IsOnCooldown)
        {
            validator.bookletPrinter.PlayErrorSound();
            return false;
        }

        NetworkedJob[] jobs = NetworkedJob.GetAllJobs()
            .Where(job => job?.Job?.State == JobState.InProgress)
            .ToArray();
        if (jobs.Length == 0)
        {
            validator.bookletPrinter.PlayErrorSound();
            return false;
        }

        running = true;
        CoroutineManager.Instance.StartCoroutine(Summon(validator, requester, jobs));
        return true;
    }

    private static IEnumerator Summon(JobValidator validator, ServerPlayer requester,
        IReadOnlyList<NetworkedJob> jobs)
    {
        DebugRuntime.Publish("job", "job.booklet-summon-started", DebugRuntimeSide.Server,
            entityType: "Player", entityId: requester.PlayerId.ToString(), data: new()
            {
                ["requestingPlayerId"] = requester.PlayerId,
                ["jobCount"] = jobs.Count
            });

        try
        {
            for (int index = 0; index < jobs.Count; index++)
            {
                NetworkedJob job = jobs[index];
                Vector3 position = validator.bookletPrinter.spawnAnchor.position +
                    Vector3.up * (0.02f * index);
                Quaternion rotation = validator.bookletPrinter.spawnAnchor.rotation;

                if (!job.TryGetIssuedJobBooklet(requester.Guid, out ushort copy))
                {
                    NetworkedItem item;
                    job.PendingBookletIssuedTo = requester.Guid;
                    try
                    {
                        JobBooklet booklet = BookletCreator.CreateJobBooklet(job.Job, position,
                            rotation, WorldMover.OriginShiftParent, true);
                        item = booklet.GetComponent<NetworkedItem>();
                    }
                    finally
                    {
                        job.PendingBookletIssuedTo = Guid.Empty;
                    }
                    Publish("job.booklet-copy-issued", job, item, requester.PlayerId);
                }
                else
                {
                    // A copy in an inventory is a row: its object is made first (D14, OI-COLD-07).
                    NetworkedJob summoned = job;
                    PlayerItems.WithObject(copy, item =>
                    {
                        Relocate(item, requester, position, rotation);
                        Publish("job.booklet-copy-summoned", summoned, item, requester.PlayerId);
                    });
                }

                validator.bookletPrinter.Print(true);
                yield return WaitFor.Seconds(1.25f);
            }
        }
        finally
        {
            running = false;
            DebugRuntime.Publish("job", "job.booklet-summon-completed",
                DebugRuntimeSide.Server, entityType: "Player",
                entityId: requester.PlayerId.ToString(), data: new()
                {
                    ["requestingPlayerId"] = requester.PlayerId,
                    ["jobCount"] = jobs.Count
                });
        }
    }

    private static void Relocate(NetworkedItem item, ServerPlayer requester,
        Vector3 worldPosition, Quaternion rotation)
    {
        if (item == null)
            return;
        ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.ItemState);
        if (snapshot == null)
            return;
        snapshot.ItemState = ItemState.Dropped;
        snapshot.PlayerId = 0;
        snapshot.ItemPosition = worldPosition - WorldMover.currentMove;
        snapshot.ItemRotation = rotation;
        snapshot.InventoryClaimSlot = -1;
        snapshot.InventoryClaimFlags = ItemInventoryClaimFlags.None;
        if (!LiveItems.TryApplyTransition(item, snapshot, requester,
                ItemTransitionReason.JobBookletSummon, true, out string rejectionReason,
                clearRetrievalClaim: true,
                cause: ItemChangeLog.Move(global::Multiplayer.Core.Items.ChangeCauses.ItemMoveReason.JobBookletSummoned, requester,
                    "to where " + requester.Username + " summoned it")))
        {
            Multiplayer.LogWarning($"Unable to summon job booklet {item.NetId}: {rejectionReason}");
            return;
        }
        item.ApplyServerCanonicalSnapshot(snapshot);
        NetworkServer.Instance.SendItemUpdate(snapshot);
    }

    private static void Publish(string eventName, NetworkedJob job, NetworkedItem item,
        byte playerId)
    {
        DebugRuntime.Publish("job", eventName, DebugRuntimeSide.Server,
            entityType: "Job", entityId: job.NetId.ToString(), data: new()
            {
                ["jobId"] = job.Job?.ID ?? string.Empty,
                ["itemNetId"] = item?.NetId ?? 0,
                ["issuedToPlayerId"] = playerId
            });
    }
}
