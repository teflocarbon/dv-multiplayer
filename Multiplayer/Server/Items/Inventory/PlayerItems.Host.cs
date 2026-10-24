using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Utils;
using Newtonsoft.Json;
using UnityEngine;

namespace Multiplayer.Server.Items.Inventory;

internal static partial class PlayerItems
{
    // The host's own player's inventory as rows (docs/design/items/inventory.md, "Stand-ins"; D14,
    // OI-COLD-11): kept as data on the server as a remote player's is, and drawn as stand-ins in the
    // host's own slots (HostInventory). Its join takes its rows up as a remote joiner's are, with no
    // object made; a put-away hands its object over to data at once, with the state it had.

    /// <summary>
    /// The host's own player put an object away (or the server put one in its slot): the item is kept as data in its
    /// slot, with the state the object had, and the object goes from every game. False if it stays
    /// an object (no persistent id yet, or a row already there).
    /// </summary>
    internal static bool StoreAsData(LiveItems.LiveItem record, NetworkedItem item, ServerPlayer holder, string state)
    {
        KeepStashedState(record.NetId, state);
        pendingData.Remove(record.NetId);
        bool had = ColdInventories.Contains(record.PersistentItemId);
        GoData(record, item, holder);
        return !had && ColdInventories.Contains(record.PersistentItemId) && item.ColdStorageRetirement;
    }

    // The host's own player's rows go live as data in their slots and are drawn as stand-ins once
    // its inventory exists, job papers too (OI-COLD-07). An item the ledger moved to Lost and Found
    // and one with no free slot are made as objects instead (TakeUp, GoLive), as for a remote joiner.
    private static IEnumerator JoinHost(ServerPlayer player)
    {
        while (DV.InventorySystem.Inventory.Instance == null && player.PlayerId != 0)
            yield return null;
        if (player.PlayerId == 0)
            yield break;
        // A paper's row goes live while its job wants it (JobPaperRows.Wanted), and the save's jobs are
        // known once their stations register: a paper whose job isn't known yet waits a while for it, so
        // it isn't dropped (and printed again), while the rest are drawn now. One whose job never comes is
        // dropped after.
        int drawn = 0;
        List<SavedItemRow> waiting = new();
        foreach (SavedItemRow row in RowsOf(player).ToArray())
        {
            if (JobUnknown(row))
                waiting.Add(row);
            else
                drawn += DrawRow(player, row);
        }
        float until = Time.realtimeSinceStartup + JobsWait;
        while (player.PlayerId != 0 && Time.realtimeSinceStartup < until && waiting.Any(JobUnknown))
            yield return null;
        if (player.PlayerId == 0)
            yield break;
        foreach (SavedItemRow row in waiting)
            drawn += DrawRow(player, row);
        if (drawn > 0)
            Multiplayer.Log($"Inventory: {drawn} item(s) of the host's own inventory are stand-ins from their rows");
    }

    private static int DrawRow(ServerPlayer player, SavedItemRow row)
    {
        if (IsTakenUp(row.PersistentItemId) || TakeUp(player, row) is not { } create)
            return 0;
        HostInventory.Draw(create, StandInState(create.ItemNetId, row.State));
        return 1;
    }

    private const float JobsWait = 10f;

    private static bool JobUnknown(SavedItemRow row) =>
        JobPapers.IsPaper(row.PrefabName) && JobPapers.JobIdOf(row.State) is { } jobId &&
        !NetworkedJob.TryGetFromJobId(jobId, out _);

    /// <summary>Whether a row of this holder hasn't gone live yet: the host's join is still drawing its inventory.</summary>
    internal static bool RowsPending(Guid holder) =>
        ColdInventories.Of(holder).Any(row => !IsTakenUp(row.PersistentItemId));

    private static bool IsTakenUp(Guid persistentId) =>
        LiveItems.Records.Any(record => record.PersistentItemId == persistentId);
}
