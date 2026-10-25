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

// An owner's recall and a slot swap: inventory moves the server commits in one step.
public static partial class LiveItems
{
    /// <summary>
    /// An owner's recall, in one step (docs/design/items/wire.md, "Recall"): the server commits it,
    /// and the update it returns, sent to everyone, empties the holder's hand and puts the item in
    /// the owner's claimed slot. Nothing is asked of the owner's game first.
    /// </summary>
    public static bool TryRecall(NetworkedItem item, ServerPlayer requester, int requestedSlot,
        uint expectedRevision, out ItemUpdate snapshot, out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        if (item == null || requester == null || item.NetId == 0 || !records.TryGetValue(item.NetId, out LiveItem record))
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }
        if (item.Item?.InventorySpecs?.IsEssential != true)
        {
            rejectionReason = "item-not-recallable";
            return false;
        }
        if (!ItemAccessServer.Allows(requester, ItemAccessAction.Recall, item, record, out rejectionReason))
            return false;
        TransitionResult result = ItemStateMachine.Recall(ToCore(record), new RecallCommand
        {
            Requester = requester.Guid,
            ExpectedRevision = expectedRevision,
            RequestedSlot = requestedSlot
        });
        if (!result.Accepted)
        {
            rejectionReason = result.RejectionReason;
            return false;
        }

        snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.ItemState);
        if (snapshot == null)
        {
            rejectionReason = "snapshot-creation-failed";
            return false;
        }
        snapshot.ItemState = ItemState.InInventory;
        snapshot.PlayerId = requester.PlayerId;

        Guid previousHolder = record.Holder;
        ItemWhereabouts before = ItemChangeLog.Whereabouts(record);
        LiveItem previous = record.Copy();
        // The holder becomes the requester's identity: left as the previous holder's, a recalled
        // item was handed back to that player when they rejoined, and the owner had a second one.
        ApplyCore(record, result.State);
        // The slot asked for (the silhouette clicked), else the claim, else a free one.
        record.Slot = ServerLedger.FreeSlot(record, requester.Guid, requestedSlot, record.InventoryClaimSlot);
        record.LastReason = ItemTransitionReason.OwnerRecall;
        ChangeCause<ItemMoveReason> recalled = ItemChangeLog.Move(ItemMoveReason.Recalled, requester,
            ItemChangeCauses.Detail(ItemChangeLog.Describe(before, ItemChangeLog.Whereabouts(record)),
                "slot " + record.Slot));
        rejectionReason = record.Slot < 0 ? "inventory-full"
            : ServerLedger.Commit(record, ServerLedger.HomeOf(previous), ServerLedger.HomeOf(record), recalled);
        if (rejectionReason.Length > 0)
        {
            record.RestoreFrom(previous);
            snapshot = null;
            return false;
        }
        WriteToSnapshot(record, snapshot, ItemTransitionReason.OwnerRecall);
        snapshot.MoveCause = recalled;
        ItemChangeLog.RecordMove(item, record, recalled);
        // Out of a moving cab, say: its lease ends with the recall, so nothing (the save) takes the
        // lease's place for it (SL4, OI-ESS-11).
        EndLease(record, "recalled");
        PlayerItems.Stored(record, sendRow: true);
        Publish("item.recall-accepted", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["previousPossessorPlayerId"] = PlayerNumbers.Of(previousHolder),
            ["previousHolder"] = previousHolder == Guid.Empty ? string.Empty : previousHolder.ToString("D"),
            ["requestedSlot"] = requestedSlot,
            ["baseRevision"] = expectedRevision
        });
        return true;
    }

    /// <summary>
    /// Two items in the requester's inventory change slots (vanilla's swap, <c>Inventory.SwapItems</c>):
    /// the ledger commits both at once, since each slot holds one item. The moved item is checked
    /// against the revision the requester saw; the other moves with it.
    /// </summary>
    public static bool TrySwapSlots(NetworkedItem item, NetworkedItem other, ServerPlayer requester,
        uint expectedRevision, out ItemUpdate first, out ItemUpdate second, out string rejectionReason)
    {
        first = second = null;
        if (item == null || other == null || requester == null || !records.TryGetValue(item.NetId, out LiveItem record) ||
            !records.TryGetValue(other.NetId, out LiveItem otherRecord) || record == otherRecord)
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }
        if (record.Placement != ItemPlacementKind.PlayerInventory || otherRecord.Placement != ItemPlacementKind.PlayerInventory ||
            record.Holder != requester.Guid || otherRecord.Holder != requester.Guid)
        {
            rejectionReason = "item-not-in-your-inventory";
            return false;
        }
        TransitionResult moved = Stay(record, expectedRevision), swapped = Stay(otherRecord, otherRecord.Revision);
        if (!moved.Accepted || !swapped.Accepted)
        {
            rejectionReason = (moved.Accepted ? swapped : moved).RejectionReason;
            return false;
        }
        first = item.CreateUpdateData(ItemUpdate.ItemUpdateType.ItemState);
        second = other.CreateUpdateData(ItemUpdate.ItemUpdateType.ItemState);
        if (first == null || second == null)
        {
            first = second = null;
            rejectionReason = "snapshot-creation-failed";
            return false;
        }
        LiveItem previous = record.Copy(), otherPrevious = otherRecord.Copy();
        ApplyCore(record, moved.State);
        ApplyCore(otherRecord, swapped.State);
        (record.Slot, otherRecord.Slot) = (otherPrevious.Slot, previous.Slot);
        ChangeCause<ItemMoveReason> cause = ItemChangeLog.Move(ItemMoveReason.SlotMoved, requester,
            $"slot {previous.Slot} <-> {otherPrevious.Slot}");
        rejectionReason = ServerLedger.Swap(record, ServerLedger.HomeOf(previous), otherRecord,
            ServerLedger.HomeOf(otherPrevious), cause);
        if (rejectionReason.Length > 0)
        {
            record.RestoreFrom(previous);
            otherRecord.RestoreFrom(otherPrevious);
            first = second = null;
            return false;
        }
        foreach ((NetworkedItem networked, LiveItem changed, ItemUpdate snapshot) in
                 new[] { (item, record, first), (other, otherRecord, second) })
        {
            snapshot.ItemState = ItemState.InInventory;
            snapshot.PlayerId = requester.PlayerId;
            changed.LastReason = ItemTransitionReason.ClientInteraction;
            WriteToSnapshot(changed, snapshot, ItemTransitionReason.ClientInteraction);
            snapshot.MoveCause = cause;
            ItemChangeLog.RecordMove(networked, changed, cause);
        }
        PlayerItems.Stored(record);
        PlayerItems.Stored(otherRecord);
        Publish("item.slots-swapped", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["otherNetId"] = other.NetId,
            ["fromSlot"] = previous.Slot,
            ["toSlot"] = otherPrevious.Slot
        });
        return true;

        // The same inventory, at the next revision: the state machine checks the revision and the possessor.
        TransitionResult Stay(LiveItem changed, uint expected) => ItemStateMachine.Apply(ToCore(changed), new ItemTransition
        {
            Actor = requester.Guid,
            ExpectedRevision = expected,
            AppliesPlacement = true,
            RequestedPlacement = LivePlacement.PlayerInventory
        });
    }
}
