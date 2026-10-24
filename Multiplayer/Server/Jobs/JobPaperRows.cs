using System;
using System.Collections.Generic;
using System.Linq;
using DV.CabControls;
using DV.ThingTypes;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Containers;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Server.Jobs;

/// <summary>
/// The server's half of job papers as rows (<see cref="JobPapers"/>; D14, OI-COLD-07, OI-COLD-15):
/// a paper in a player's inventory is data only, kept by its job under its NetId, saved with its
/// holder and back in their slot after a restart. Its job decides whether it's still wanted.
/// </summary>
internal static class JobPaperRows
{
    /// <summary>
    /// Whether a paper's row can go live in its holder's slot: a booklet while its job is in
    /// progress, an overview while its job is offered and no one else has its overview (one the
    /// job printed again on the board at the load gives way). A report outlives its job.
    /// </summary>
    public static bool Wanted(SavedItemRow row, out NetworkedJob job)
    {
        job = null;
        if (row.PrefabName == nameof(JobReport))
            return true;
        if (JobPapers.JobIdOf(row.State) is not { } jobId || !NetworkedJob.TryGetFromJobId(jobId, out job) || job.Job == null)
            return false;
        if (row.PrefabName == nameof(JobBooklet))
            return job.Job.State == JobState.InProgress;
        return job.Job.State == JobState.Available && (job.JobOverviewNetId == 0 || OnBoard(job.JobOverview));
    }

    private static bool OnBoard(NetworkedItem overview) =>
        overview != null && LiveItems.TryGet(overview.NetId, out LiveItems.LiveItem record) &&
        record.Placement == ItemPlacementKind.World;

    /// <summary>A paper's row went live in a player's slot: its job keeps it by its NetId.</summary>
    public static void Admit(LiveItems.LiveItem record, string prefabName, ServerPlayer holder, NetworkedJob job)
    {
        if (job == null || record == null)
            return;
        if (prefabName == nameof(JobBooklet))
        {
            job.RegisterBookletRow(record.NetId, holder?.PlayerId ?? 0);
            return;
        }
        if (prefabName != nameof(JobOverview))
            return;
        if (job.JobOverview is { } board && board.NetId != record.NetId)
        {
            Multiplayer.Log($"Job {job.Job.ID}: the overview printed on the board again goes; {holder?.Username}'s is the job's");
            board.GetTrackedItem<JobOverview>()?.DestroyJobOverview();
        }
        job.SetOverviewRow(record.NetId);
    }

    /// <summary>A paper's row that isn't wanted goes, from the cold store and the ledger.</summary>
    public static void Drop(SavedItemRow row, string why)
    {
        ColdInventories.Remove(row.PersistentItemId);
        ServerLedger.Ledger.End(row.PersistentItemId, ItemRemovalReason.JobDocumentDestroyed, detail: why);
        Multiplayer.Log($"Job papers: {row.PrefabName} ({row.PersistentItemId:D}) of {ItemChangeLog.PlayerName(row.PlacementPlayerIdentity)} goes: {why}");
    }

    /// <summary>
    /// A paper's NetId goes (its holder left, and it went cold): its job no longer keeps it by
    /// that NetId, so its row is wanted again when they're back and no NetId used again later
    /// stands for it.
    /// </summary>
    public static void Forget(ushort netId)
    {
        if (netId == 0)
            return;
        foreach (NetworkedJob job in NetworkedJob.GetAllJobs())
        {
            if (job.JobOverviewNetId == netId)
                job.SetOverviewRow(0);
            job.UnregisterJobBooklet(netId);
        }
    }

    /// <summary>Whether a booklet of the job is kept in any player's inventory, here or not, or in a box.</summary>
    public static bool HasBookletRow(string jobId) =>
        jobId != null && (ColdInventories.All().Any(row => row.PrefabName == nameof(JobBooklet) && JobPapers.JobIdOf(row.State) == jobId) ||
                          StoredPapers(jobId).Any(stored => stored.PrefabName == nameof(JobBooklet)));

    // The job's booklets and overview kept in boxes (a folder, a registrator: containers.md).
    private static IEnumerable<StoredItemRecord> StoredPapers(string jobId) =>
        ContainerServer.Graph.ExportSnapshot().Items.Where(stored => ItemSaveLoad.IsJobSystemDocument(stored.PrefabName) &&
                                                                     JobPapers.JobIdOf(ItemSaveLoad.RowOfStored(stored).State) == jobId);

    /// <summary>A paper in a box that isn't wanted goes, from its box and the ledger.</summary>
    public static void DropStored(Guid id, string why)
    {
        StoredItemRecord stored = ContainerServer.Graph.TakeOut(id, out string refused);
        if (stored == null)
        {
            Multiplayer.LogWarning($"Job papers: {id:D} stays in its box: {refused}");
            return;
        }
        ServerLedger.Ledger.End(id, ItemRemovalReason.JobDocumentDestroyed, detail: why);
        Multiplayer.Log($"Job papers: {stored.PrefabName} ({id:D}) in box {stored.ParentContainerId:D} goes: {why}");
    }

    /// <summary>
    /// A job ended (completed, abandoned, expired): its booklets and overview in inventories go as
    /// its objects do. One a player here holds is made and destroyed, so their game is told; one an
    /// absent player holds is dropped from the cold store. One in a box goes from the box.
    /// </summary>
    public static void OnJobEnded(NetworkedJob job)
    {
        string jobId = job?.Job?.ID;
        if (jobId == null)
            return;
        foreach (LiveItems.LiveItem record in LiveItems.Records.ToArray())
        {
            if (!PlayerItems.IsDataOnly(record) || !ItemSaveLoad.IsJobSystemDocument(record.PrefabName) ||
                !ColdInventories.TryGet(record.PersistentItemId, out SavedItemRow row) || JobPapers.JobIdOf(row.State) != jobId)
                continue;
            PlayerItems.WithObject(record.NetId, Destroy);
        }
        foreach (SavedItemRow row in ColdInventories.All().ToArray())
            if (ItemSaveLoad.IsJobSystemDocument(row.PrefabName) && JobPapers.JobIdOf(row.State) == jobId &&
                !LiveItems.Records.Any(record => record.PersistentItemId == row.PersistentItemId))
                Drop(row, $"its job {jobId} is over");
        foreach (StoredItemRecord stored in StoredPapers(jobId).ToArray())
            DropStored(stored.PersistentItemId, $"its job {jobId} is over");
    }

    // Out of its slot first: vanilla's DestroyJobOverview is a bare Destroy, which would leave the
    // host's slot pointing at it.
    private static void Destroy(NetworkedItem item)
    {
        if (item == null)
            return;
        NetworkedJob.RemoveFromInventory(item.GetComponent<ItemBase>(), item.NetId);
        if (item.GetTrackedItem<JobBooklet>() is { } booklet)
            booklet.DestroyJobBooklet();
        else if (item.GetTrackedItem<JobOverview>() is { } overview)
            overview.DestroyJobOverview();
    }
}
