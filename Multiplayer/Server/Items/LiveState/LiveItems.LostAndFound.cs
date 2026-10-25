using DV.Booklets;
using DV.InventorySystem;
using Multiplayer.Core.Items.Access;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LiveState;
using Multiplayer.Core.Items.Physics;
using Multiplayer.Server.Items.Access;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.Ledger;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Items.SaveLoad;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Debugging;
using Multiplayer.Shared.Items.ChangeCauses;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Jobs;
using Multiplayer.Shared.Protocol.Data;
using Multiplayer.Shared.Protocol.Data.Items;
using Multiplayer.Shared.Session;
using Multiplayer.Shared.Trains;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Core.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Server.Items.LiveState;

// Into and out of Lost and Found, and a refused collection put back.
public static partial class LiveItems
{
    /// <summary>
    /// Whose Lost and Found an item goes to: its owner's, or for a job booklet, which nobody owns (D5),
    /// the player it was issued to, by identity (OI-LF-17). Empty when nobody's: an unowned item never
    /// goes to a Lost and Found.
    /// </summary>
    public static Guid KeeperOf(LiveItem record)
    {
        if (record == null || record.Owner != Guid.Empty)
            return record?.Owner ?? Guid.Empty;
        return record.PrefabName == nameof(JobBooklet) && NetworkedItem.TryGet(record.NetId, out NetworkedItem item) &&
               item != null && item.GetTrackedItem<JobBooklet>()?.job is { } job &&
               NetworkedJob.TryGetFromJob(job, out NetworkedJob networkedJob) &&
               networkedJob.TryGetIssuedPlayer(record.NetId, out Guid issued) ? issued : Guid.Empty;
    }

    public static bool TryMoveToLostAndFound(NetworkedItem item, out LiveItem record,
        out string rejectionReason, bool allowRecoveryPlacement = false)
    {
        record = null;
        rejectionReason = string.Empty;
        if (item == null || item.NetId == 0 || !records.TryGetValue(item.NetId, out record))
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }
        if (KeeperOf(record) == Guid.Empty)
        {
            rejectionReason = "item-has-no-persistent-owner";
            return false;
        }
        if (record.Placement != ItemPlacementKind.World &&
            (!allowRecoveryPlacement || record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory))
        {
            rejectionReason = "item-not-in-world";
            return false;
        }
        if (record.Revision == uint.MaxValue)
        {
            rejectionReason = "authority-revision-exhausted";
            return false;
        }
        rejectionReason = ServerLedger.Commit(record, ServerLedger.HomeOf(record), Home.LostAndFound,
            ItemChangeLog.Move(ItemMoveReason.CollectedToLostAndFound, detail: "from " + record.Placement));
        if (rejectionReason.Length > 0)
            return false;

        record.Revision++;
        // Out of its tool's magazine first (containers.md, "Ammo in a magazine").
        if (record.Placement == ItemPlacementKind.Magazine && item.Item?.InContainer is DV.Items.ItemMagazine magazine)
            magazine.RemoveItem(item.gameObject, false, false);
        record.MagazineNetId = 0;
        record.MagazineToolId = Guid.Empty;
        record.MagazineSlot = -1;
        // Off its coupler or snap point first, as a deleted car does (OI-LF-04, OI-LIST-14).
        item.LeaveSnapPoint();
        record.AttachedCarNetId = 0;
        record.Placement = ItemPlacementKind.LostAndFound;
        record.Slot = -1;
        record.Holder = Guid.Empty;
        record.PlacementHand = PlayerHand.None;
        record.LastReason = ItemTransitionReason.LostAndFoundCollection;
        // Collected mid-flight: its lease ends with the collection (OI-PHY-02, -03).
        EndLease(record, "lost-and-found");
        Publish("item.lost-and-found-authority-applied", item, record, null);
        return true;
    }

    public static bool TryRetrieveFromLostAndFound(NetworkedItem item, ServerPlayer requester,
        uint expectedRevision, int targetSlot, out ItemUpdate snapshot, out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        if (item == null || requester == null || item.NetId == 0 ||
            !records.TryGetValue(item.NetId, out LiveItem record))
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }
        if (record.Placement != ItemPlacementKind.LostAndFound)
        {
            rejectionReason = "item-not-in-lost-and-found";
            return false;
        }
        // The entry's keeper: its owner, or the player a booklet was issued to (OI-LF-17).
        Guid keeper = record.Owner != Guid.Empty ? record.Owner : LostAndFoundServer.KeeperOf(item.NetId);
        if (keeper == Guid.Empty || keeper != requester.Guid)
        {
            rejectionReason = "requester-not-persistent-owner";
            return false;
        }
        if (record.Revision != expectedRevision)
        {
            rejectionReason = "stale-lost-item-revision";
            return false;
        }
        if (record.Revision == uint.MaxValue)
        {
            rejectionReason = "authority-revision-exhausted";
            return false;
        }

        ItemUpdate created = item.CreateUpdateData(ItemUpdate.ItemUpdateType.Create);
        if (created == null)
        {
            rejectionReason = "snapshot-creation-failed";
            return false;
        }
        ItemWhereabouts before = ItemChangeLog.Whereabouts(record);
        LiveItem previous = record.Copy();
        record.Revision++;
        record.Placement = ItemPlacementKind.PlayerInventory;
        record.Holder = requester.Guid;
        record.PlacementHand = PlayerHand.None;
        record.Slot = ServerLedger.FreeSlot(record, requester.Guid, targetSlot);
        record.InventoryClaimFlags &= ~(ItemInventoryClaimFlags.Dropped | ItemInventoryClaimFlags.Stolen);
        record.LastReason = ItemTransitionReason.LostAndFoundRetrieval;
        ChangeCause<ItemMoveReason> retrieved = ItemChangeLog.Move(ItemMoveReason.RetrievedFromLostAndFound,
            requester, ItemChangeCauses.Detail(ItemChangeLog.Describe(before, ItemChangeLog.Whereabouts(record)),
                "slot " + record.Slot));
        rejectionReason = record.Slot < 0 ? "inventory-full"
            : ServerLedger.Commit(record, ServerLedger.HomeOf(previous), ServerLedger.HomeOf(record), retrieved);
        if (rejectionReason.Length > 0)
        {
            record.RestoreFrom(previous);
            return false;
        }

        created.ItemState = ItemState.InInventory;
        created.PlayerId = requester.PlayerId;
        WriteToSnapshot(record, created, ItemTransitionReason.LostAndFoundRetrieval);
        created.MoveCause = retrieved;
        ItemChangeLog.RecordMove(item, record, retrieved);
        snapshot = created;
        PlayerItems.Stored(record, sendRow: true);
        Publish("item.lost-and-found-retrieval-authority-applied", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["targetSlot"] = record.Slot
        });
        return true;
    }

    /// <summary>Puts an item's record in its owner's Lost and Found, connected or not.</summary>
    public static LiveItem RestoreLostRecord(NetworkedItem item, Guid owner, uint revision,
        string prefabName, int inventoryClaimSlot = -1,
        ItemInventoryClaimFlags inventoryClaimFlags = ItemInventoryClaimFlags.None,
        ItemMoveReason reason = ItemMoveReason.HostCorrection)
    {
        if (item == null || item.NetId == 0 || owner == Guid.Empty)
            return null;
        LiveItem record = Ensure(item);
        record.Revision = revision;
        record.ObjectStateRunBase = record.ObjectStateRunTop = 0;
        record.Placement = ItemPlacementKind.LostAndFound;
        record.Slot = -1;
        record.Holder = Guid.Empty;
        record.PlacementHand = PlayerHand.None;
        record.StoredClaimSlot = inventoryClaimSlot;
        record.InventoryClaimFlags = inventoryClaimFlags;
        record.PrefabName = string.IsNullOrWhiteSpace(prefabName) ? record.PrefabName : prefabName;
        record.LastReason = ItemTransitionReason.LostAndFoundCollection;
        ChangeCause<ItemMoveReason> cause = ItemChangeLog.Move(reason,
            detail: "in Lost and Found, owner " + ItemChangeLog.PlayerName(owner) +
                    (reason == ItemMoveReason.HostCorrection ? ", retrieval rolled back" : null));
        ServerLedger.Settle(record, owner, inventoryClaimSlot, cause);
        ItemChangeLog.RecordMove(item, record, cause);
        item.ApplyAuthorityMetadata(AuthorityMetadata(record, record.Revision, record.LastReason));
        return record;
    }

    /// <summary>
    /// A refused collection: the record is again all it was before TryMoveToLostAndFound (its car,
    /// magazine, slot, holder and revision; J4), and the ledger with it. Clients never saw the
    /// collection. An item it took off a coupler, a snap point or a tool's magazine is off it now, so
    /// the caller puts it in the world where it is.
    /// </summary>
    public static void RollbackLostAndFoundCollection(NetworkedItem item, LiveItem previous)
    {
        if (item == null || previous == null || !records.TryGetValue(item.NetId, out LiveItem record) ||
            record.Placement != ItemPlacementKind.LostAndFound)
            return;
        record.RestoreFrom(previous);
        ChangeCause<ItemMoveReason> rolledBack = ItemChangeLog.Move(ItemMoveReason.HostCorrection,
            detail: "Lost and Found collection rolled back to " + record.Placement);
        ServerLedger.Commit(record, Home.LostAndFound, ServerLedger.HomeOf(record), rolledBack);
        ItemChangeLog.RecordMove(item, record, rolledBack);
        item.ApplyAuthorityMetadata(AuthorityMetadata(record, record.Revision, record.LastReason));
    }
}
