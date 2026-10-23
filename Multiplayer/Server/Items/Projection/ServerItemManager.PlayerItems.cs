using System;
using System.Collections.Generic;
using System.Linq;
using DV.InventorySystem;
using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Utils;
using Multiplayer.Debugging.Protocol;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Multiplayer.Server.Items.Projection;

internal sealed partial class ServerItemManager
{
    // What players bring, take back and leave with: a returning player's originals, and a
    // departing player's held items. A recall, the host's player's too, is a request
    // (NetworkServer.Items, "Recall").

    /// <summary>
    /// Host: a disconnecting player can't keep holding anything. Commit each item in their hand
    /// as dropped where it is, so the authority record matches the object lying in the world
    /// and anyone can pick it up. Their own inventory items stay keyed to them for when they
    /// return. Items in their hand or inventory that belong to someone else go to their owner's
    /// Lost and Found, whether the owner is online or not (owned-items.md, "Leaving").
    /// </summary>
    internal void DropHeldItemsOfDepartingPlayer(ServerPlayer player)
    {
        if (player == null || !NetworkLifecycle.Instance.IsHost())
            return;

        foreach (LiveItems.LiveItem record in LiveItems.Records.ToArray())
        {
            if (record.Holder != player.Guid)
                continue;
            bool inHand = record.Placement == ItemPlacementKind.PlayerHand;
            bool borrowed = record.Placement == ItemPlacementKind.PlayerInventory && IsSomeoneElses(record, player);
            if (!inHand && !borrowed)
                continue;
            if (!NetworkedItem.TryGet(record.NetId, out NetworkedItem item) || item == null)
                continue;

            ItemUpdate snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
            if (snapshot == null)
                continue;
            snapshot.ItemState = ItemState.Dropped;
            snapshot.PlayerId = 0;
            snapshot.PlayerHand = PlayerHand.None;
            snapshot.ItemPosition = item.transform.position - WorldMover.currentMove;
            snapshot.ItemRotation = item.transform.rotation;
            snapshot.AuthorityRevision = record.Revision;
            if (!LiveItems.TryApplyTransition(item, snapshot, player,
                    ItemTransitionReason.HostLocalState, true, out string rejection,
                    cause: ItemChangeLog.Move(ItemMoveReason.DroppedOnDisconnect, player,
                        inHand ? "was in their hand" : "borrowed, owner " +
                            ItemChangeLog.PlayerName(record.Owner))))
            {
                Multiplayer.LogWarning($"Could not drop item {record.NetId} held by departing player {player.Username}: {rejection}");
                continue;
            }
            item.ApplyServerCanonicalSnapshot(snapshot);
            RegisterHostWorldItem(item);
            NetworkServer.Instance.SendItemUpdate(snapshot, excludePlayer: player);
            // Someone else's item goes straight to its owner's Lost and Found, online or not, rather
            // than lie where the borrower stood (owned-items.md, "Leaving").
            if (IsSomeoneElses(record, player))
                LostAndFoundServer.Collect(item, LostItemReason.BorrowerLeft, "left by " + player.Username);
        }
    }

    private static bool IsSomeoneElses(LiveItems.LiveItem record, ServerPlayer player) =>
        record.Owner != Guid.Empty && record.Owner != player.Guid;

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static bool IsFinite(Quaternion value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
        !float.IsNaN(value.w) && !float.IsInfinity(value.w);

}
