using DV.InventorySystem;
using Multiplayer.Debugging;
using Multiplayer.Debugging.Protocol;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

/// <summary>
/// Host-owned source of truth for item identity, placement, persistent ownership and
/// inventory claims. Unity object fields are projections of these records, never peers.
/// </summary>
public static class AuthoritativeItemRegistry
{
    public sealed class Record
    {
        public ushort NetId;
        public uint Revision;
        public ItemPlacementKind Placement;
        public byte PlacementPlayerId;
        public byte PersistentOwnerPlayerId;
        public byte InventoryClaimPlayerId;
        public int InventoryClaimSlot = -1;
        public ItemInventoryClaimFlags InventoryClaimFlags;
        public string PrefabName = string.Empty;
        public Vector3 Position;
        public Quaternion Rotation;
        public ItemTransitionReason LastReason;
    }

    private static readonly Dictionary<ushort, Record> records = new();

    public static bool TryGet(ushort netId, out Record record) => records.TryGetValue(netId, out record);

    public static Record Ensure(NetworkedItem item, ServerPlayer actor = null, ItemUpdateData seed = null)
    {
        if (item == null || item.NetId == 0)
            return null;
        if (records.TryGetValue(item.NetId, out Record existing))
            return existing;

        bool persistentlyPlayerOwned = item.Item?.IsEssential() == true ||
            item.Item?.InventorySpecs?.BelongsToPlayer == true;
        byte claimedOwner = seed?.PersistentOwnerPlayerId ?? 0;
        byte persistentOwner = actor != null && persistentlyPlayerOwned &&
            (claimedOwner == 0 || claimedOwner == actor.PlayerId)
                ? actor.PlayerId
                : (byte)0;

        Record record = new()
        {
            NetId = item.NetId,
            Placement = PlacementFrom(seed?.ItemState ?? item.DebugCurrentState),
            PlacementPlayerId = seed?.PlayerId ?? 0,
            PersistentOwnerPlayerId = persistentOwner,
            InventoryClaimPlayerId = seed?.InventoryClaimPlayerId ?? 0,
            InventoryClaimSlot = seed?.InventoryClaimSlot ?? -1,
            InventoryClaimFlags = seed?.InventoryClaimFlags ?? ItemInventoryClaimFlags.None,
            PrefabName = item.Item?.InventorySpecs?.ItemPrefabName ?? item.name,
            Position = seed?.ItemPosition ?? item.transform.position - WorldMover.currentMove,
            Rotation = seed?.ItemRotation ?? item.transform.rotation,
            LastReason = ItemTransitionReason.InitialRegistration
        };
        records[item.NetId] = record;
        if (persistentOwner != 0 && actor?.PlayerId == persistentOwner)
            actor.AddOwnedItem(item.NetId);
        Publish("item.canonical-record-created", item, record, null);
        return record;
    }

    public static bool TryApplyTransition(NetworkedItem item, ItemUpdateData snapshot, ServerPlayer actor,
        ItemTransitionReason reason, bool force, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (item == null || snapshot == null || actor == null || item.NetId == 0)
        {
            rejectionReason = "invalid-transition-input";
            return false;
        }

        Record record = Ensure(item, actor, snapshot);
        Publish("item.transition-requested", item, record, new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["requestedState"] = snapshot.ItemState.ToString(),
            ["requestedRevision"] = snapshot.AuthorityRevision,
            ["force"] = force,
            ["reason"] = reason.ToString()
        });

        if (!force && snapshot.AuthorityRevision != record.Revision)
        {
            rejectionReason = "stale-authority-revision";
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }

        ItemUpdateData.ItemUpdateType placementFlags = ItemUpdateData.ItemUpdateType.Create |
            ItemUpdateData.ItemUpdateType.ItemState | ItemUpdateData.ItemUpdateType.FullSync;
        bool appliesPlacement = (snapshot.UpdateType & placementFlags) != 0;
        ItemPlacementKind requestedPlacement = appliesPlacement ? PlacementFrom(snapshot.ItemState) : record.Placement;
        if (appliesPlacement && !force && !ActorMayTransition(record, requestedPlacement, actor.PlayerId))
        {
            rejectionReason = "sender-not-current-possessor";
            PublishRejected(item, record, actor, snapshot, rejectionReason);
            return false;
        }

        if (appliesPlacement && record.PersistentOwnerPlayerId == 0 && item.Item?.IsEssential() == true)
            record.PersistentOwnerPlayerId = actor.PlayerId;

        if (appliesPlacement)
        {
            record.Placement = requestedPlacement;
            record.PlacementPlayerId = requestedPlacement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory
                ? actor.PlayerId
                : (byte)0;
        }

        // Only the persistent owner can modify the retained recall claim. Borrowing an
        // essential item never transfers its reserved slot or recall right.
        if (appliesPlacement && record.PersistentOwnerPlayerId != 0 && actor.PlayerId == record.PersistentOwnerPlayerId)
        {
            if (snapshot.InventoryClaimSlot >= 0)
            {
                record.InventoryClaimPlayerId = actor.PlayerId;
                if (record.InventoryClaimSlot < 0 || item.Item?.IsEssential() != true)
                    record.InventoryClaimSlot = snapshot.InventoryClaimSlot;
                else if (record.InventoryClaimSlot != snapshot.InventoryClaimSlot)
                    Publish("item.essential-claim-slot-move-rejected", item, record, new()
                    {
                        ["requestedSlot"] = snapshot.InventoryClaimSlot,
                        ["retainedSlot"] = record.InventoryClaimSlot
                    }, DebugSeverity.Warning);
                record.InventoryClaimFlags = snapshot.InventoryClaimFlags;
            }
            if (requestedPlacement == ItemPlacementKind.PlayerInventory)
                record.InventoryClaimFlags &= ~(ItemInventoryClaimFlags.Dropped | ItemInventoryClaimFlags.Stolen);
            else if (record.InventoryClaimSlot >= 0)
            {
                record.InventoryClaimFlags |= ItemInventoryClaimFlags.Dropped | ItemInventoryClaimFlags.Reserved;
                record.InventoryClaimFlags &= ~ItemInventoryClaimFlags.Stolen;
            }
        }
        else if (appliesPlacement && record.PersistentOwnerPlayerId != 0 &&
                 actor.PlayerId != record.PersistentOwnerPlayerId && record.InventoryClaimSlot >= 0)
        {
            record.InventoryClaimFlags |= ItemInventoryClaimFlags.Dropped |
                ItemInventoryClaimFlags.Reserved | ItemInventoryClaimFlags.Stolen;
        }

        record.Position = snapshot.ItemPosition;
        record.Rotation = snapshot.ItemRotation;
        record.LastReason = reason;
        record.Revision++;
        WriteToSnapshot(record, snapshot, reason);
        Publish("item.transition-accepted", item, record, new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["reason"] = reason.ToString()
        });
        return true;
    }

    public static bool TryRecall(NetworkedItem item, ServerPlayer requester, int requestedSlot,
        uint expectedRevision, out ItemUpdateData snapshot, out string rejectionReason)
    {
        snapshot = null;
        rejectionReason = string.Empty;
        if (item == null || requester == null || item.NetId == 0 || !records.TryGetValue(item.NetId, out Record record))
        {
            rejectionReason = "unknown-network-entity";
            return false;
        }
        if (record.PersistentOwnerPlayerId == 0 || record.PersistentOwnerPlayerId != requester.PlayerId)
        {
            rejectionReason = "requester-not-persistent-owner";
            return false;
        }
        if (item.Item?.IsEssential() != true)
        {
            rejectionReason = "item-not-recallable";
            return false;
        }
        if (expectedRevision != record.Revision)
        {
            rejectionReason = "stale-authority-revision";
            return false;
        }

        ItemUpdateData recallSnapshot = item.CreateUpdateData(ItemUpdateData.ItemUpdateType.ItemState);
        if (recallSnapshot == null)
        {
            rejectionReason = "snapshot-creation-failed";
            return false;
        }

        byte previousPossessor = record.PlacementPlayerId;
        record.Placement = ItemPlacementKind.PlayerInventory;
        record.PlacementPlayerId = requester.PlayerId;
        record.InventoryClaimPlayerId = requester.PlayerId;
        if (requestedSlot >= 0)
            record.InventoryClaimSlot = requestedSlot;
        record.InventoryClaimFlags |= ItemInventoryClaimFlags.Reserved;
        record.InventoryClaimFlags &= ~(ItemInventoryClaimFlags.Dropped | ItemInventoryClaimFlags.Stolen);
        record.LastReason = ItemTransitionReason.OwnerRecall;
        record.Revision++;

        snapshot = recallSnapshot;
        snapshot.ItemState = ItemState.InInventory;
        snapshot.PlayerId = requester.PlayerId;
        WriteToSnapshot(record, snapshot, ItemTransitionReason.OwnerRecall);
        Publish("item.recall-accepted", item, record, new()
        {
            ["requestingPlayerId"] = requester.PlayerId,
            ["previousPossessorPlayerId"] = previousPossessor,
            ["requestedSlot"] = requestedSlot
        });
        return true;
    }

    public static void ApplyReplicaMetadata(NetworkedItem item, ItemUpdateData snapshot)
    {
        if (item == null || snapshot == null || item.NetId == 0)
            return;
        item.ApplyAuthorityMetadata(snapshot);
    }

    public static void WriteToSnapshot(Record record, ItemUpdateData snapshot, ItemTransitionReason reason)
    {
        snapshot.AuthorityRevision = record.Revision;
        snapshot.PersistentOwnerPlayerId = record.PersistentOwnerPlayerId;
        snapshot.InventoryClaimPlayerId = record.InventoryClaimPlayerId;
        snapshot.InventoryClaimSlot = record.InventoryClaimSlot;
        snapshot.InventoryClaimFlags = record.InventoryClaimFlags;
        snapshot.TransitionReason = reason;
        snapshot.PlayerId = record.PlacementPlayerId;
    }

    public static Dictionary<string, object> Snapshot(Record record)
    {
        if (record == null)
            return new();
        return new Dictionary<string, object>
        {
            ["netId"] = record.NetId,
            ["revision"] = record.Revision,
            ["placement"] = record.Placement.ToString(),
            ["placementPlayerId"] = record.PlacementPlayerId,
            ["persistentOwnerPlayerId"] = record.PersistentOwnerPlayerId,
            ["inventoryClaimPlayerId"] = record.InventoryClaimPlayerId,
            ["inventoryClaimSlot"] = record.InventoryClaimSlot,
            ["inventoryClaimFlags"] = record.InventoryClaimFlags.ToString(),
            ["prefabName"] = record.PrefabName,
            ["lastReason"] = record.LastReason.ToString()
        };
    }

    public static void Remove(ushort netId) => records.Remove(netId);
    public static void Clear() => records.Clear();

    private static bool ActorMayTransition(Record record, ItemPlacementKind requested, byte actor)
    {
        if (record.Placement is ItemPlacementKind.PlayerHand or ItemPlacementKind.PlayerInventory)
            return record.PlacementPlayerId == 0 || record.PlacementPlayerId == actor;
        return true;
    }

    private static ItemPlacementKind PlacementFrom(ItemState state) => state switch
    {
        ItemState.InHand => ItemPlacementKind.PlayerHand,
        ItemState.InInventory => ItemPlacementKind.PlayerInventory,
        ItemState.Attached => ItemPlacementKind.Attached,
        _ => ItemPlacementKind.World
    };

    private static void PublishRejected(NetworkedItem item, Record record, ServerPlayer actor,
        ItemUpdateData snapshot, string reason)
    {
        Publish("item.transition-rejected", item, record, new()
        {
            ["actorPlayerId"] = actor.PlayerId,
            ["requestedState"] = snapshot.ItemState.ToString(),
            ["requestedRevision"] = snapshot.AuthorityRevision,
            ["rejectionReason"] = reason
        }, DebugSeverity.Warning);
    }

    private static void Publish(string eventName, NetworkedItem item, Record record,
        Dictionary<string, object> additional, DebugSeverity severity = DebugSeverity.Info)
    {
        if (!DebugRuntime.EnabledFor("item") || item == null)
            return;
        Dictionary<string, object> data = Snapshot(record);
        if (additional != null)
            foreach (KeyValuePair<string, object> pair in additional)
                data[pair.Key] = pair.Value;
        DebugRuntime.Publish("item", eventName, DebugRuntimeSide.Server, severity,
            "Item", item.NetId.ToString(), data);
    }
}
