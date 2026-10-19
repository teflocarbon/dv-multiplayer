using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Components.Networking.World;

/// <summary>
/// Why items moved, changed hands or went away (<see cref="ItemMoveReason"/>,
/// <see cref="ItemRemovalReason"/>): builds the causes, logs each change in one line on whichever
/// peer sees it, and on the host keeps the recent removals, whose records are gone. A record keeps
/// its last move (<see cref="AuthoritativeItemRegistry.Record.LastMove"/>).
/// </summary>
public static class ItemChangeLog
{
    private const int CallerFrames = 6;

    /// <summary>Host: the last items removed, for "why did it vanish?" once the record is gone.</summary>
    public static readonly RecentItemRemovals RecentRemovals = new();

    public static ChangeCause<ItemMoveReason> Move(ItemMoveReason reason, ServerPlayer by = null,
        string detail = null) =>
        new(reason, by?.PlayerId ?? 0, by?.Username, NetworkLifecycle.Instance?.Tick ?? 0, detail);

    public static ChangeCause<ItemRemovalReason> Removal(ItemRemovalReason reason, ServerPlayer by = null,
        string detail = null) =>
        new(reason, by?.PlayerId ?? 0, by?.Username, NetworkLifecycle.Instance?.Tick ?? 0, detail);

    public static ItemWhereabouts Whereabouts(AuthoritativeItemRegistry.Record record) =>
        record == null ? default : new ItemWhereabouts((AuthorityPlacement)(byte)record.Placement,
            record.PlacementPlayerId, record.PersistentOwnerPlayerId);

    /// <summary>What changed between two whereabouts, with the host's player names.</summary>
    public static string Describe(ItemWhereabouts before, ItemWhereabouts after) =>
        ItemMoveClassifier.Describe(before, after, PlayerName);

    /// <summary>Joins what a reason leaves out: the change itself, and anything the caller adds.</summary>
    public static string Detail(params string[] parts)
    {
        List<string> kept = new();
        foreach (string part in parts)
            if (!string.IsNullOrEmpty(part))
                kept.Add(part);
        return string.Join("; ", kept);
    }

    /// <summary>Host: logs a move and keeps it as the record's last.</summary>
    public static void RecordMove(NetworkedItem item, AuthoritativeItemRegistry.Record record,
        ChangeCause<ItemMoveReason> cause)
    {
        if (record == null)
            return;
        record.LastMove = cause;
        Multiplayer.Log(ItemChangeLogFormat.Moved(Name(item, record), record.NetId, cause));
    }

    /// <summary>Host: logs a removal and keeps it among the recent ones.</summary>
    public static void RecordRemoval(NetworkedItem item, ChangeCause<ItemRemovalReason> cause)
    {
        if (item == null || item.NetId == 0)
            return;
        AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record);
        RecentRemovals.Add(new ItemRemovalRecord(item.NetId, Name(item, record), Whereabouts(record),
            cause, record?.LastMove ?? default));
        Multiplayer.Log(ItemChangeLogFormat.Removed(Name(item, record), item.NetId, cause));
    }

    /// <summary>
    /// Client: logs the cause a host update carries and keeps it on the item. Changes to the local
    /// player's items go to the log; everyone else's to the debug log, or a busy yard floods it.
    /// </summary>
    public static void OnClientSnapshot(NetworkedItem item, ItemUpdateData snapshot)
    {
        if (snapshot == null)
            return;
        byte self = NetworkLifecycle.Instance?.Client?.PlayerId ?? 0;
        bool mine = self != 0 && (snapshot.PersistentOwnerPlayerId == self || item != null &&
            (item.PersistentOwnerPlayerId == self || item.playerBelongsToId == self));
        string name = item != null ? item.name : snapshot.PrefabName;
        string line;
        if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Destroy)
        {
            ChangeCause<ItemRemovalReason> cause = snapshot.RemovalCause ??
                (snapshot.TransitionReason == ItemTransitionReason.InterestRetirement
                    ? new ChangeCause<ItemRemovalReason>(ItemRemovalReason.LeftInterest)
                    : new ChangeCause<ItemRemovalReason>(ItemRemovalReason.Unknown, detail: snapshot.TransitionReason.ToString()));
            item?.SetLastRemovalCause(cause);
            line = ItemChangeLogFormat.Removed(name, snapshot.ItemNetId, cause);
            if (cause.Reason == ItemRemovalReason.LeftInterest && !InLocalPossession(item, self))
                mine = false;
        }
        else if (snapshot.MoveCause is { } move)
        {
            mine |= self != 0 && snapshot.PlayerId == self;
            item?.SetLastMoveCause(move);
            line = ItemChangeLogFormat.Moved(name, snapshot.ItemNetId, move);
        }
        else
            return;
        if (mine)
            Multiplayer.Log(line);
        else
            Multiplayer.LogDebug(() => line);
    }

    /// <summary>
    /// The methods that led here, outside this class and the given types: what a change no caller
    /// explained can still say about where it came from.
    /// </summary>
    public static string Callers(params Type[] skip)
    {
        var names = new List<string>();
        foreach (StackFrame frame in new StackTrace(1, false).GetFrames() ?? Array.Empty<StackFrame>())
        {
            MethodBase method = frame.GetMethod();
            Type type = method?.DeclaringType;
            if (method == null || type == typeof(ItemChangeLog) || Array.IndexOf(skip, type) >= 0)
                continue;
            names.Add(type != null ? $"{type.Name}.{method.Name}" : method.Name);
            if (names.Count == CallerFrames)
                break;
        }
        string callers = names.Count > 0 ? string.Join(" < ", names) : "an unknown caller";
        return NetworkLifecycle.Instance?.IsProcessingPacket == true ? "in a packet handler, " + callers : callers;
    }

    /// <summary>
    /// Host: a job booklet, overview or report is about to be destroyed by the job system. Its
    /// callers are still on the stack here, unlike in OnDestroy, so they say which job event did it.
    /// </summary>
    public static void NoteJobDocumentRemoval(UnityEngine.Component document, DV.Logic.Job.Job job, Type patch)
    {
        if (document == null || NetworkLifecycle.Instance?.IsHost() != true ||
            !document.TryGetComponent(out NetworkedItem item) || item.NetId == 0)
            return;
        item.NoteRemovalCause(Removal(ItemRemovalReason.JobDocumentDestroyed, detail: Detail(
            job != null ? $"job {job.ID} {job.State}" : "no job", "callers " + Callers(patch))));
    }

    public static string PlayerName(byte playerId) =>
        playerId != 0 && NetworkLifecycle.Instance?.Server != null &&
        NetworkLifecycle.Instance.Server.TryGetServerPlayer(playerId, out ServerPlayer player)
            ? player.Username : null;

    private static bool InLocalPossession(NetworkedItem item, byte self) =>
        item != null && self != 0 && item.playerBelongsToId == self;

    private static string Name(NetworkedItem item, AuthoritativeItemRegistry.Record record) =>
        item != null ? item.name : record?.PrefabName;
}
