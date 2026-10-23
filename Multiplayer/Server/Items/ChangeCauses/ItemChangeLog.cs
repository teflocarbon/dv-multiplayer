using System;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.ChangeCauses;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Session;

namespace Multiplayer.Server.Items.ChangeCauses;

/// <summary>
/// Why items moved, changed hands or went away (<see cref="ItemMoveReason"/>,
/// <see cref="ItemRemovalReason"/>), on the host: builds the causes with the players who made them,
/// logs each change in one line, and keeps the recent removals, whose records are gone. A record
/// keeps its last move (<see cref="LiveItems.LiveItem.LastMove"/>). What every game
/// has, the details and a client's log line, is <see cref="ItemChangeCauses"/>.
/// </summary>
public static class ItemChangeLog
{
    /// <summary>Host: the last items removed, for "why did it vanish?" once the record is gone.</summary>
    public static readonly RecentItemRemovals RecentRemovals = new();

    public static ChangeCause<ItemMoveReason> Move(ItemMoveReason reason, ServerPlayer by = null,
        string detail = null) =>
        new(reason, by?.PlayerId ?? 0, by?.Username, NetworkLifecycle.Instance?.Tick ?? 0, detail);

    public static ChangeCause<ItemRemovalReason> Removal(ItemRemovalReason reason, ServerPlayer by = null,
        string detail = null) =>
        new(reason, by?.PlayerId ?? 0, by?.Username, NetworkLifecycle.Instance?.Tick ?? 0, detail);

    /// <summary>
    /// Host: a job booklet, overview or report is about to be destroyed by the job system. Its
    /// callers are still on the stack here, unlike in OnDestroy, so they say which job event did it.
    /// </summary>
    public static void NoteJobDocumentRemoval(UnityEngine.Component document, DV.Logic.Job.Job job, Type patch)
    {
        if (document == null || NetworkLifecycle.Instance?.IsHost() != true ||
            !document.TryGetComponent(out NetworkedItem item) || item.NetId == 0)
            return;
        item.NoteRemovalCause(new ChangeCause<ItemRemovalReason>(ItemRemovalReason.JobDocumentDestroyed,
            tick: NetworkLifecycle.Instance?.Tick ?? 0, detail: ItemChangeCauses.Detail(
                job != null ? $"job {job.ID} {job.State}" : "no job",
                "callers " + ItemChangeCauses.Callers(patch, typeof(ItemChangeLog)))));
    }

    public static ItemWhereabouts Whereabouts(LiveItems.LiveItem record) =>
        record == null ? default : new ItemWhereabouts((LivePlacement)(byte)record.Placement,
            record.Holder, record.Owner);

    /// <summary>What changed between two whereabouts, with the host's player names.</summary>
    public static string Describe(ItemWhereabouts before, ItemWhereabouts after) =>
        ItemMoveClassifier.Describe(before, after, identity => PlayerNumbers.Session(identity)?.Username);

    /// <summary>Host: logs a move and keeps it as the record's last.</summary>
    public static void RecordMove(NetworkedItem item, LiveItems.LiveItem record,
        ChangeCause<ItemMoveReason> cause)
    {
        if (record == null)
            return;
        record.LastMove = cause;
        Multiplayer.Log(ItemChangeLogFormat.Moved(Name(item, record), record.NetId, cause));
        ServerLedger.OnMove(record, cause);
    }

    /// <summary>Host: logs a removal and keeps it among the recent ones.</summary>
    public static void RecordRemoval(NetworkedItem item, ChangeCause<ItemRemovalReason> cause)
    {
        if (item == null || item.NetId == 0)
            return;
        LiveItems.TryGet(item.NetId, out LiveItems.LiveItem record);
        RecentRemovals.Add(new ItemRemovalRecord(item.NetId, Name(item, record), Whereabouts(record),
            cause, record?.LastMove ?? default));
        Multiplayer.Log(ItemChangeLogFormat.Removed(Name(item, record), item.NetId, cause));
        ServerLedger.OnRemoved(item, cause);
    }

    public static string PlayerName(byte playerId) =>
        playerId != 0 && NetworkServer.Instance != null &&
        NetworkServer.Instance.TryGetServerPlayer(playerId, out ServerPlayer player)
            ? player.Username : null;

    /// <summary>A player's name while they're connected, else their identity as the logs show it.</summary>
    public static string PlayerName(Guid identity) =>
        identity == Guid.Empty ? "none" : PlayerNumbers.Session(identity)?.Username ?? PlayerIdentity.Describe(identity);

    private static string Name(NetworkedItem item, LiveItems.LiveItem record) =>
        item != null ? item.name : record?.PrefabName;
}
