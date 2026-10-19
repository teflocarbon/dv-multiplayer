using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;

namespace Multiplayer.Shared.Items;

/// <summary>
/// The parts of "why did this item change" (<see cref="ItemMoveReason"/>,
/// <see cref="ItemRemovalReason"/>) that every game has: the details a cause carries, and the log
/// line a client writes for the cause a host update brings. The host's own log of moves and
/// removals is the server's <c>ItemChangeLog</c>.
/// </summary>
public static class ItemChangeCauses
{
    private const int CallerFrames = 6;

    /// <summary>Joins what a reason leaves out: the change itself, and anything the caller adds.</summary>
    public static string Detail(params string[] parts)
    {
        List<string> kept = new();
        foreach (string part in parts)
            if (!string.IsNullOrEmpty(part))
                kept.Add(part);
        return string.Join("; ", kept);
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
            if (method == null || type == typeof(ItemChangeCauses) || Array.IndexOf(skip, type) >= 0)
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
        item.NoteRemovalCause(new ChangeCause<ItemRemovalReason>(ItemRemovalReason.JobDocumentDestroyed,
            tick: NetworkLifecycle.Instance?.Tick ?? 0, detail: Detail(
                job != null ? $"job {job.ID} {job.State}" : "no job", "callers " + Callers(patch))));
    }

    /// <summary>
    /// Client: logs the cause a host update carries and keeps it on the item. Changes to the local
    /// player's items go to the log; everyone else's to the debug log, or a busy yard floods it.
    /// </summary>
    public static void OnClientSnapshot(NetworkedItem item, ItemUpdateData snapshot, byte localPlayerId)
    {
        if (snapshot == null)
            return;
        byte self = localPlayerId;
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

    private static bool InLocalPossession(NetworkedItem item, byte self) =>
        item != null && self != 0 && item.playerBelongsToId == self;
}
