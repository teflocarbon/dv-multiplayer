using Multiplayer.Core.Diagnostics;
using Multiplayer.Core.Items.ChangeCauses;
using Multiplayer.Core.Items.Ledger;
using Multiplayer.Core.Items.LostAndFound;
using Multiplayer.Server.Items.ChangeCauses;
using Multiplayer.Server.Items.Containers;
using Multiplayer.Server.Items.Inventory;
using Multiplayer.Server.Items.LiveState;
using Multiplayer.Server.Items.LostAndFound;
using Multiplayer.Server.Items.OwnedList;
using Multiplayer.Server.Items.Projection;
using Multiplayer.Server.Session;
using Multiplayer.Shared.Items.LiveState;
using Multiplayer.Shared.Protocol.Data.Items;
using System;
using System.Collections.Generic;

namespace Multiplayer.Server.Items.Ledger;

/// <summary>
/// The one-home repair in a Release build (D7; docs/design/items/ledger.md, "The invariant check"):
/// the ledger wins on where. An owned item found somewhere other than its ledger home is put there;
/// where the server can't put it there, it goes to its owner's Lost and Found (<c>SaveRecovery</c>).
/// </summary>
public static partial class ServerLedger
{
    private const string RepairDetail = "invariant repair";
    // The items one check has repaired: each is repaired once, by its first breach.
    private static readonly HashSet<Guid> repaired = new();

    private static void Repair(InvariantViolation violation)
    {
        Guid id = violation.Id;
        // A box mid-deposit or mid-withdrawal is seen part-way: it waits for the next check.
        if (id != Guid.Empty && !ContainerServer.Graph.IsSettled(id))
            return;
        if (id != Guid.Empty && violation.Rule != InvariantRule.OneItemPerSlot && !repaired.Add(id))
            return;
        LiveItems.LiveItem record = id == Guid.Empty ? null : RecordOf(id);
        switch (violation.Rule)
        {
            case InvariantRule.LiveAgrees when record != null:
                PutHome(record);
                break;
            case InvariantRule.OneHome when violation.Repair is { Kind: HomeKind.LostAndFound }:
                RemakeLost(id);
                break;
            case InvariantRule.OneHome:
                KeepOneCopy(id);
                break;
            case InvariantRule.NoStray when record != null && ledger.TryGet(id, out LedgerEntry ended) && !ended.IsLive:
                foreach (LiveItems.LiveItem copy in Copies(id))
                    Remint(copy, id, ended.Owner.Kind == OwnerKind.Player ? ended.Owner.Id : Guid.Empty,
                        $"its entry ended ({ended.Ended.Reason})");
                break;
            case InvariantRule.NoStray when record != null && record.StoredOwner != Guid.Empty:
                Own(record, record.StoredOwner, BirthReason.Claimed, Guid.Empty, RepairDetail);
                break;
            case InvariantRule.OneItemPerSlot when violation.Repair is { Kind: HomeKind.Slot } slot:
                OneInSlot(slot);
                break;
            // A clash of claims (no slot named): the first registered keeps it, the others move
            // to their owner's next free claim, or none; each moved claim is sent (OnLedgerChanged).
            case InvariantRule.OneItemPerSlot when ledger.TryGet(id, out LedgerEntry claimant) && claimant.IsLive:
                ledger.SettleClaimClash(claimant.Owner, claimant.ClaimSlot,
                    new LedgerCause(ItemMoveReason.HostCorrection, Guid.Empty, RepairDetail));
                break;
            case InvariantRule.VanillaAgrees when record != null:
                MirrorLists(record);
                break;
        }
        // Not repaired: a stray or a copy that is a row, with no object to move (the load re-mints a
        // copy and registers a stray: ledger.md, "Births and ends"); a cold row that disagrees: its
        // holder's join makes it again, and the next check (at the latest, before the save) puts it
        // home.
    }

    /// <summary>
    /// The item goes to its ledger home: into its holder's slot (a cold row when they're offline),
    /// or into Lost and Found. A home the ledger keeps no pose for (the ground, a train, a shelf), a
    /// container's slot or an install the server can't make, and a slot it can't take: the owner's
    /// Lost and Found. An item on a coupler or a snap point or loaded in a tool is taken off as a
    /// retrieval takes it off (OI-LIST-14), by way of its owner's Lost and Found. An installed one
    /// stays, and the ledger follows: gadget removal is paused (OI-LIST-15).
    /// </summary>
    internal static void PutHome(LiveItems.LiveItem record)
    {
        if (ledger.TryGetHome(record.PersistentItemId, out Home want))
            PutHome(record, want);
    }

    private static void PutHome(LiveItems.LiveItem record, Home want)
    {
        Guid id = record.PersistentItemId;
        Home live = HomeOf(record);
        LedgerCause follow = new(ItemMoveReason.HostCorrection, Guid.Empty, RepairDetail);
        if (live.Kind == HomeKind.Installed)
        {
            ledger.Repair(id, live, follow);
            Multiplayer.LogWarning($"Invariant repair: {record.PrefabName} ({id:D}) is {live}, not {want}; " +
                                   "an installed item can't be taken off yet, so the ledger follows");
            return;
        }
        if (live.Kind is HomeKind.Attached or HomeKind.Snapped or HomeKind.Magazine)
        {
            TakeOff(record, live, want, follow);
            return;
        }
        PlayerItems.WithObject(record.NetId, item =>
        {
            if (item == null || !LiveItems.TryGet(item.NetId, out LiveItems.LiveItem now) || now.PersistentItemId != id)
                return;
            // Each move below commits from where the item is.
            ledger.Repair(id, HomeOf(now), follow);
            string refused = Put(item, now, want);
            if (refused.Length == 0)
            {
                Multiplayer.Log($"Invariant repair: {now.PrefabName} ({id:D}) put back in {want}");
                return;
            }
            if (want.Kind != HomeKind.LostAndFound && now.Owner != Guid.Empty &&
                ToLostAndFound(item, now, out string lost))
            {
                Multiplayer.Log($"Invariant repair: {now.PrefabName} ({id:D}) couldn't go to {want} ({refused}); " +
                                "it went to its owner's Lost and Found");
                return;
            }
            Multiplayer.LogWarning($"Invariant repair: {now.PrefabName} ({id:D}) couldn't go to {want} ({refused}); " +
                                   $"it stays {HomeOf(now)}, and the ledger follows");
        });
    }

    private static string Put(NetworkedItem item, LiveItems.LiveItem record, Home want)
    {
        switch (want.Kind)
        {
            case HomeKind.LostAndFound:
                return ToLostAndFound(item, record, out string refused) ? string.Empty : refused;
            case HomeKind.Slot:
            case HomeKind.Hand:
                ServerPlayer holder = PlayerNumbers.Session(want.Holder);
                if (holder == null)
                    return want.Kind == HomeKind.Slot && PlayerItems.GoColdInto(record, want.Holder, want.Slot) &&
                           ledger.Repair(record.PersistentItemId, want,
                               new LedgerCause(ItemMoveReason.HostCorrection, Guid.Empty, RepairDetail)).Committed
                        ? string.Empty : "holder-offline";
                // A hand is put back as a slot: the server doesn't put things in a player's hands.
                int slot = FreeSlot(record, holder.Guid, want.Kind == HomeKind.Slot ? want.Slot : -1);
                return slot < 0 ? "inventory-full" : IntoSlot(item, record, holder, slot);
            default:
                return "not-a-home-the-server-can-put-it-in";
        }
    }

    // Into the holder's slot: out of Lost and Found as a recall, from another player's hands by way of
    // the ground (a take from a player is refused, OI-ACC-07), from anywhere else directly.
    private static string IntoSlot(NetworkedItem item, LiveItems.LiveItem record, ServerPlayer holder, int slot)
    {
        ItemUpdate snapshot;
        string refused;
        if (record.Placement == ItemPlacementKind.LostAndFound)
        {
            if (!LostAndFoundServer.TryRetrieveForRecall(holder, item, record.Revision, slot, out snapshot, out refused))
                return refused;
        }
        else
        {
            if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory &&
                !Retrieval.DropFromHolder(item, record, PlayerNumbers.Session(record.Holder) ?? holder,
                    ItemChangeLog.Move(ItemMoveReason.HostCorrection, holder, RepairDetail), out refused))
                return refused;
            snapshot = item.CreateUpdateData(ItemUpdate.ItemUpdateType.FullSync);
            if (snapshot == null)
                return "snapshot-creation-failed";
            snapshot.ItemState = ItemState.InInventory;
            snapshot.PlayerId = holder.PlayerId;
            snapshot.PlayerHand = PlayerHand.None;
            snapshot.InventorySlot = slot;
            snapshot.InventoryClaimSlot = record.InventoryClaimSlot;
            snapshot.AuthorityRevision = record.Revision;
            if (!LiveItems.TryApplyTransition(item, snapshot, holder, ItemTransitionReason.HostLocalState, true,
                    out refused, cause: ItemChangeLog.Move(ItemMoveReason.HostCorrection, holder,
                        $"{RepairDetail}, slot {slot}")))
                return refused;
            LiveItems.WriteToSnapshot(record, snapshot, ItemTransitionReason.HostLocalState);
            item.ApplyServerCanonicalSnapshot(snapshot);
            ServerItemManager.Instance?.RegisterHostWorldItem(item);
        }
        snapshot.OriginatingPlayerId = 0;
        NetworkServer.Instance.SendItemUpdate(snapshot);
        return string.Empty;
    }

    // From a player's hands by way of the ground, then collected (SendToLostAndFound refuses an item
    // with a player).
    private static bool ToLostAndFound(NetworkedItem item, LiveItems.LiveItem record, out string refused)
    {
        refused = string.Empty;
        if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory &&
            !Retrieval.DropFromHolder(item, record, PlayerNumbers.Session(record.Holder),
                ItemChangeLog.Move(ItemMoveReason.HostCorrection, null, RepairDetail), out refused))
            return false;
        return SendToLostAndFound(record.PersistentItemId, LostItemReason.SaveRecovery, RepairDetail, out refused);
    }
}
