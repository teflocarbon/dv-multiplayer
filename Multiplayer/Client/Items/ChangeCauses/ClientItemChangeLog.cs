using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;

namespace Multiplayer.Client.Items.ChangeCauses;

/// <summary>
/// Why the host says this game's copies of items changed: the cause a host update carries, logged
/// and kept on the item. The details every game builds causes from are <see cref="ItemChangeCauses"/>;
/// the host's own log is the server's <c>ItemChangeLog</c>.
/// </summary>
internal static class ClientItemChangeLog
{
    /// <summary>
    /// Logs the cause a host update carries and keeps it on the item. Changes to the local
    /// player's items go to the log; everyone else's to the debug log, or a busy yard floods it.
    /// </summary>
    internal static void OnSnapshot(NetworkedItem item, ItemUpdate snapshot, byte localPlayerId)
    {
        if (snapshot == null)
            return;
        byte self = localPlayerId;
        bool mine = self != 0 && (snapshot.Relation == ItemRelation.Mine || item != null &&
            (item.Relation == ItemRelation.Mine || item.playerBelongsToId == self));
        string name = item != null ? item.name : snapshot.PrefabName;
        string line;
        if (snapshot.UpdateType == ItemUpdate.ItemUpdateType.Destroy)
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
