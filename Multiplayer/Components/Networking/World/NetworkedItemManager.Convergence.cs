using System.Collections.Generic;
using System.Linq;
using Multiplayer.Components.Networking.World.WorldItems;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

public partial class NetworkedItemManager
{
    // Host convergence ("anti-entropy"). Item replication is event-driven: once a client knows an
    // item, it only hears about it again when the host marks it dirty. In a retrofit of a
    // single-player game some vanilla code paths will always move things without the mod
    // noticing, and without a correction pass every such drift was permanent. Re-assert the
    // host's view of a few world items per player, round-robin, so drift heals within seconds.
    // Clients ignore a convergence snapshot unless their copy actually disagrees.

    private const float ConvergenceIntervalSeconds = 0.1f; // per player: ~10 items/s
    private float nextConvergenceAt;
    private readonly Dictionary<byte, int> convergenceCursor = new();

    private void RunConvergence()
    {
        if (Time.unscaledTime < nextConvergenceAt)
            return;
        nextConvergenceAt = Time.unscaledTime + ConvergenceIntervalSeconds;

        var server = NetworkLifecycle.Instance.Server;
        foreach (ServerPlayer player in server.ServerPlayers)
        {
            if (player.Peer == server.SelfPeer || player.LoadingState < PlayerLoadingState.Complete)
                continue;
            NetworkedItem item = NextConvergenceCandidate(player);
            if (item != null && TryCreateConvergenceSnapshot(item, out ItemUpdateData snapshot))
                server.SendItemUpdatePacket(snapshot, sendToPlayer: player);
        }
    }

    private NetworkedItem NextConvergenceCandidate(ServerPlayer player)
    {
        if (player.KnownItems.Count == 0)
            return null;
        NetworkedItem[] known = player.KnownItems.Keys.ToArray();
        convergenceCursor.TryGetValue(player.PlayerId, out int cursor);
        for (int tried = 0; tried < known.Length; tried++)
        {
            NetworkedItem candidate = known[(cursor + tried) % known.Length];
            if (!IsConvergenceCandidate(candidate))
                continue;
            convergenceCursor[player.PlayerId] = cursor + tried + 1;
            return candidate;
        }
        convergenceCursor[player.PlayerId] = cursor + known.Length;
        return null;
    }

    // World-placed and at rest only: held, stored, lost-and-found and in-flight (leased) items
    // have their own authority flows that a re-sent snapshot could race with.
    private bool IsConvergenceCandidate(NetworkedItem item) =>
        item != null && item.NetId != 0 &&
        AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record) &&
        record.Placement is ItemPlacementKind.World or ItemPlacementKind.TrainInterior or ItemPlacementKind.StaticParent &&
        !HasActiveSpatialState(item.NetId) &&
        !NetworkedLostAndFoundManager.Contains(item.NetId) &&
        !WorldItemPersistenceManager.IsRestorePending(item);

    // The host's live object is what clients should match (the record's pose can lag, e.g. for
    // items riding in cars); authority metadata comes from the record.
    private static bool TryCreateConvergenceSnapshot(NetworkedItem item, out ItemUpdateData snapshot)
    {
        snapshot = null;
        if (!AuthoritativeItemRegistry.TryGet(item.NetId, out AuthoritativeItemRegistry.Record record))
            return false;
        snapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState | ItemUpdateData.ItemUpdateType.ItemPosition);
        if (snapshot == null)
            return false;
        snapshot.ItemState = ItemState.Dropped;
        snapshot.AuthorityRevision = record.Revision;
        snapshot.PersistentOwnerPlayerId = record.PersistentOwnerPlayerId;
        snapshot.PlayerId = 0;
        snapshot.PlayerHand = PlayerHand.None;
        snapshot.OriginatingPlayerId = 0;
        snapshot.TransitionReason = ItemTransitionReason.Convergence;
        return true;
    }
}
